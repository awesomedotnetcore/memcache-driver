﻿using System;
using System.Collections.Concurrent;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Threading;

using Criteo.Memcache.Requests;
using Criteo.Memcache.Authenticators;
using Criteo.Memcache.Headers;
using Criteo.Memcache.Exceptions;

namespace Criteo.Memcache.Transport
{
    internal class MemcacheTransport : IMemcacheTransport
    {
        #region Events

        public event Action<Exception> TransportError;

        public event Action<MemcacheResponseHeader, IMemcacheRequest> MemcacheError;

        public event Action<MemcacheResponseHeader, IMemcacheRequest> MemcacheResponse;

        public event Action<IMemcacheTransport> TransportDead;

        #endregion Events

        private readonly int _queueTimeout;
        private readonly int _pendingLimit;

        // TODO : add me in the Conf
        private readonly int _windowSize = 2 << 15;

        // END TODO
        private readonly EndPoint _endPoint;

        private readonly IMemcacheAuthenticator _authenticator;
        private readonly Action<MemcacheTransport> _transportAvailable;

        private const int CONNECT_TIMER_PERIOD_MS = 1000;
        private readonly Timer _connectTimer;

        private volatile bool _disposed = false;
        private volatile bool _initialized = false;
        private BlockingCollection<IMemcacheRequest> _pendingRequests;
        private ConcurrentQueue<IMemcacheRequest> _pendingQueue;
        private Socket _socket;
        private CancellationTokenSource _token;

        private SocketAsyncEventArgs _sendAsynchEvtArgs;
        private SocketAsyncEventArgs _receiveHeaderAsynchEvtArgs;
        private SocketAsyncEventArgs _receiveBodyAsynchEvtArgs;

        private MemcacheResponseHeader _currentResponse;

        public bool IsAlive { get; private set; }

        /// <summary>
        /// Ctor, intialize things ...
        /// </summary>
        /// <param name="endpoint" />
        /// <param name="authenticator">Object that ables to Sasl authenticate the socket</param>
        /// <param name="queueTimeout" />
        /// <param name="pendingLimit" />
        /// <param name="tranportAvailable">Delegate to call when the transport is alive</param>
        /// <param name="planToConnect">If true, connects in a timer started immedialty then call the transportAvailable
        /// else will connect synchronously at the first requests</param>
        public MemcacheTransport(EndPoint endpoint, IMemcacheAuthenticator authenticator, int queueTimeout, int pendingLimit, Action<MemcacheTransport> tranportAvailable, bool planToConnect)
        {
            IsAlive = false;
            _endPoint = endpoint;
            _authenticator = authenticator;
            _queueTimeout = queueTimeout;
            _transportAvailable = tranportAvailable;
            _connectTimer = new Timer(TryConnect);
            _initialized = false;
            _pendingLimit = pendingLimit;

            _pendingQueue = new ConcurrentQueue<IMemcacheRequest>();
            _pendingRequests = _pendingLimit > 0 ?
                new BlockingCollection<IMemcacheRequest>(_pendingQueue, _pendingLimit) :
                new BlockingCollection<IMemcacheRequest>(_pendingQueue);

            _sendAsynchEvtArgs = new SocketAsyncEventArgs();
            _sendAsynchEvtArgs.Completed += OnSendRequestComplete;

            _receiveHeaderAsynchEvtArgs = new SocketAsyncEventArgs();
            _receiveHeaderAsynchEvtArgs.SetBuffer(new byte[MemcacheResponseHeader.SIZE], 0, MemcacheResponseHeader.SIZE);
            _receiveHeaderAsynchEvtArgs.Completed += new EventHandler<SocketAsyncEventArgs>(OnReadResponseComplete);

            _receiveBodyAsynchEvtArgs = new SocketAsyncEventArgs();
            _receiveBodyAsynchEvtArgs.Completed += OnReceiveBodyComplete;

            if (planToConnect)
                _connectTimer.Change(0, Timeout.Infinite);
        }

        /// <summary>
        /// Synchronously sends a request
        /// </summary>
        /// <param name="request" />
        public bool TrySend(IMemcacheRequest request)
        {
            if (request == null || _disposed)
                return false;

            if (!_initialized && !Initialize())
                return false;

            return SendRequest(request);
        }

        /// <summary>
        /// Dispose the socket
        /// 5 clean tries with 1 second interval, after it dispose it in a more dirty way
        /// </summary>
        public virtual void Dispose()
        {
            // block the start of any resets, then shut down
            if (!_disposed)
                lock (this)
                    if (!_disposed)
                    {
                        if (TransportDead != null)
                            TransportDead(this);

                        int attempt = 0;

                        while (true)
                        {
                            try
                            {
                                if (_pendingRequests.Count == 0 || ++attempt > 5)
                                {
                                    ShutDown();
                                    _disposed = true;
                                    break;
                                }
                            }
                            catch (Exception e2)
                            {
                                if (TransportError != null)
                                    TransportError(e2);
                                _disposed = true;
                                break;
                            }

                            Thread.Sleep(1000);
                        }
                        _pendingRequests.Dispose();
                    }
        }

        private void CreateSocket()
        {
            var socket = new Socket(_endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(_endPoint);

            // Set me in conf
            socket.ReceiveBufferSize = _windowSize;
            socket.SendBufferSize = _windowSize;

            if (_socket != null)
                _socket.Dispose();
            _socket = socket;
        }

        private void FailPending()
        {
            IMemcacheRequest request;
            while (_pendingRequests.TryTake(out request))
                request.Fail();
        }

        private bool Initialize()
        {
            try
            {
                lock (this)
                {
                    if (!_initialized && !_disposed)
                    {
                        CreateSocket();
                        Start();

                        _initialized = true;
                    }
                }
            }
            catch (Exception e2)
            {
                if (TransportError != null)
                    TransportError(e2);

                _connectTimer.Change(CONNECT_TIMER_PERIOD_MS, Timeout.Infinite);

                return false;
            }

            return true;
        }

        private IMemcacheRequest UnstackToMatch(MemcacheResponseHeader header)
        {
            IMemcacheRequest result = null;

            if (header.Opcode.IsQuiet())
            {
                throw new MemcacheException("No way we can match a quiet request !");
            }
            else
            {
                // hacky case on partial response for stat command
                if (header.Opcode == Opcode.Stat && header.TotalBodyLength != 0 && header.Status == Status.NoError)
                {
                    if (!_pendingQueue.TryPeek(out result))
                        throw new MemcacheException("Received a response when no request is pending");
                }
                else
                {
                    if (!_pendingRequests.TryTake(out result))
                        throw new MemcacheException("Received a response when no request is pending");
                }

                if (result.RequestId != header.Opaque)
                {
                    result.Fail();
                    throw new MemcacheException("Received a response that doesn't match with the sent request queue : sent " + result.ToString() + " received " + header.ToString());
                }
            }

            return result;
        }

        #region Reads

        private void ReadResponse()
        {
            try
            {
                _receiveHeaderAsynchEvtArgs.SetBuffer(0, MemcacheResponseHeader.SIZE);
                if (!_socket.ReceiveAsync(_receiveHeaderAsynchEvtArgs))
                    OnReadResponseComplete(_socket, _receiveHeaderAsynchEvtArgs);
            }
            catch (Exception e)
            {
                TransportFailureOnReceive(e);
            }
        }

        private void OnReadResponseComplete(object sender, SocketAsyncEventArgs args)
        {
            var socket = (Socket)sender;
            try
            {
                // if the socket has been disposed, don't raise an error
                if (args.SocketError == SocketError.OperationAborted || args.SocketError == SocketError.ConnectionAborted)
                    return;
                if (args.SocketError != SocketError.Success)
                    throw new SocketException((int)args.SocketError);

                // check if we read a full header, else continue
                if (args.BytesTransferred + args.Offset < MemcacheResponseHeader.SIZE)
                {
                    int offset = args.BytesTransferred + args.Offset;
                    args.SetBuffer(offset, MemcacheResponseHeader.SIZE - offset);
                    if (!socket.ReceiveAsync(args))
                        OnReadResponseComplete(socket, args);
                    return;
                }

                ReceiveBody(socket, args.Buffer);
            }
            catch (Exception e)
            {
                TransportFailureOnReceive(e);
            }
        }

        #endregion Reads

        private static ArraySegment<byte> EmptySegment = new ArraySegment<byte>(new byte[0]);

        private void ReceiveBody(Socket socket, byte[] headerBytes)
        {
            _currentResponse = new MemcacheResponseHeader(headerBytes);

            var body = _currentResponse.TotalBodyLength == 0 ? null : new byte[_currentResponse.TotalBodyLength];

            _receiveBodyAsynchEvtArgs.SetBuffer(body, 0, (int)_currentResponse.TotalBodyLength);

            try
            {
                if (_currentResponse.TotalBodyLength == 0 || !_socket.ReceiveAsync(_receiveBodyAsynchEvtArgs))
                    OnReceiveBodyComplete(_socket, _receiveBodyAsynchEvtArgs);
            }
            catch (Exception e)
            {
                TransportFailureOnReceive(e);
            }
        }

        private void OnReceiveBodyComplete(object sender, SocketAsyncEventArgs args)
        {
            var socket = (Socket)sender;
            try
            {
                // if the socket has been disposed, don't raise an error
                if (args.SocketError == SocketError.OperationAborted || args.SocketError == SocketError.ConnectionAborted)
                    return;
                if (args.SocketError != SocketError.Success)
                    throw new SocketException((int)args.SocketError);

                // check if we read a full header, else continue
                if (args.BytesTransferred + args.Offset < _currentResponse.TotalBodyLength)
                {
                    int offset = args.BytesTransferred + args.Offset;
                    args.SetBuffer(offset, (int)_currentResponse.TotalBodyLength - offset);
                    if (!socket.ReceiveAsync(args))
                        OnReadResponseComplete(socket, args);
                    return;
                }
                // should assert we have the good request
                var request = UnstackToMatch(_currentResponse);

                if (MemcacheResponse != null)
                    MemcacheResponse(_currentResponse, request);
                if (_currentResponse.Status != Status.NoError && MemcacheError != null)
                    MemcacheError(_currentResponse, request);

                string key = null;
                if (_currentResponse.KeyLength > 0)
                    key = UTF8Encoding.Default.GetString(args.Buffer, 0, _currentResponse.KeyLength);

                byte[] extra = null;
                if (_currentResponse.ExtraLength == _currentResponse.TotalBodyLength)
                    extra = args.Buffer;
                else if(_currentResponse.ExtraLength > 0)
                {
                    extra = new byte[_currentResponse.ExtraLength];
                    Array.Copy(args.Buffer, _currentResponse.KeyLength, extra, 0, _currentResponse.ExtraLength);
                }

                var payloadLength = _currentResponse.TotalBodyLength - _currentResponse.KeyLength - _currentResponse.ExtraLength;
                byte[] payload = null;
                if (payloadLength == _currentResponse.TotalBodyLength)
                    payload = args.Buffer;
                else if (payloadLength > 0)
                {
                    payload = new byte[payloadLength];
                    Array.Copy(args.Buffer, _currentResponse.KeyLength + _currentResponse.ExtraLength, payload, 0, payloadLength);
                }

                if (request != null)
                    request.HandleResponse(_currentResponse, key, extra, payload);

                // loop the read on the socket
                ReadResponse();
            }
            catch (Exception e)
            {
                TransportFailureOnReceive(e);
            }
        }

        private void TransportFailureOnReceive(Exception e)
        {
            if (!_disposed)
                lock (this)
                    if (!_disposed)
                    {
                        if (TransportError != null)
                            TransportError(e);
                        _socket.Disconnect(false);

                        FailPending();
                    }
        }

        private void Start()
        {
            _token = new CancellationTokenSource();

            ReadResponse();
            Authenticate();

            IsAlive = true;
        }

        private void ShutDown()
        {
            IsAlive = false;
            try
            {
                if (_token != null)
                    _token.Cancel();
            }
            catch (AggregateException e)
            {
                if (TransportError != null)
                    TransportError(e);
            }


            var socket = _socket;
            if (socket != null)
                socket.Dispose();

            if (_receiveHeaderAsynchEvtArgs != null)
                _receiveHeaderAsynchEvtArgs.Dispose();

            FailPending();
        }

        private bool Authenticate()
        {
            bool authDone = false;
            IMemcacheRequest request = null;
            Status authStatus = Status.NoError;

            if (_authenticator != null)
            {
                var mre = new ManualResetEventSlim();
                var authenticationToken = _authenticator.CreateToken();
                while (authenticationToken != null && !authDone)
                {
                    authStatus = authenticationToken.StepAuthenticate(out request);

                    switch (authStatus)
                    {
                        // auth OK, clear the token
                        case Status.NoError:
                            authenticationToken = null;
                            authDone = true;
                            break;

                        case Status.StepRequired:
                            if (request == null)
                                throw new AuthenticationException("Unable to authenticate : step required but no request from token");
                            if (!SendRequest(request, mre))
                                throw new AuthenticationException("Unable to authenticate : unable to send authentication request");
                            break;

                        default:
                            throw new AuthenticationException("Unable to authenticate : status " + authStatus.ToString());
                    }
                }
                mre.Wait();
            }

            return true;
        }

        private void TryConnect(object dummy)
        {
            try
            {
                if (Initialize() && _transportAvailable != null)
                    _transportAvailable(this);
            }
            catch (Exception e)
            {
                if (TransportError != null)
                    TransportError(e);

                _connectTimer.Change(CONNECT_TIMER_PERIOD_MS, Timeout.Infinite);
            }
        }


        private bool SendAsynch(byte[] buffer, int offset, int count, ManualResetEventSlim callAvailable)
        {
            _sendAsynchEvtArgs.UserToken = callAvailable;

            _sendAsynchEvtArgs.SetBuffer(buffer, offset, count);
            if (!_socket.SendAsync(_sendAsynchEvtArgs))
            {
                OnSendRequestComplete(_socket, _sendAsynchEvtArgs);
                return false;
            }

            return true;
        }

        private void OnSendRequestComplete(object sender, SocketAsyncEventArgs args)
        {
            try
            {
                var socket = (Socket)sender;

                if (args.SocketError != SocketError.Success)
                    throw new SocketException((int)args.SocketError);

                // check if we read a full header, else continue
                if (args.BytesTransferred + args.Offset < args.Buffer.Length)
                {
                    int offset = args.BytesTransferred + args.Offset;
                    args.SetBuffer(offset, args.Buffer.Length - offset);
                    if (!socket.SendAsync(args))
                        OnSendRequestComplete(socket, args);
                    return;
                }

                if (args.UserToken == null)
                    _transportAvailable(this);
                else
                    (args.UserToken as ManualResetEventSlim).Set();
            }
            catch (Exception e)
            {
                TransportFailureOnSend(e);
            }
        }

        private bool SendRequest(IMemcacheRequest request, ManualResetEventSlim callAvailable = null)
        {
            byte[] buffer;
            try
            {
                buffer = request.GetQueryBuffer();

                if (!_pendingRequests.TryAdd(request, _queueTimeout, _token.Token))
                {
                    if (TransportError != null)
                        TransportError(new MemcacheException("Send request queue full to " + _endPoint));

                    if (_transportAvailable != null)
                        _transportAvailable(this);
                    return false;
                }

                SendAsynch(buffer, 0, buffer.Length, callAvailable);
            }
            catch (Exception e)
            {
                TransportFailureOnSend(e);
                return false;
            }

            return true;
        }

        private void TransportFailureOnSend(Exception e)
        {
            if (!_disposed)
                lock (this)
                    if (!_disposed)
                    {
                        if (TransportError != null)
                            TransportError(e);

                        new MemcacheTransport(_endPoint, _authenticator, _queueTimeout, _pendingLimit, _transportAvailable, true);

                        FailPending();
                        Dispose();
                    }
        }

        public bool Registered { get { return TransportDead != null; } }

        public override string ToString()
        {
            return "MemcacheTransport " + _endPoint + " " + GetHashCode();
        }
    }
}
