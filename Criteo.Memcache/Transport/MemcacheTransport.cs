﻿/* Licensed to the Apache Software Foundation (ASF) under one
   or more contributor license agreements.  See the NOTICE file
   distributed with this work for additional information
   regarding copyright ownership.  The ASF licenses this file
   to you under the Apache License, Version 2.0 (the
   "License"); you may not use this file except in compliance
   with the License.  You may obtain a copy of the License at

     http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing,
   software distributed under the License is distributed on an
   "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
   KIND, either express or implied.  See the License for the
   specific language governing permissions and limitations
   under the License.
*/
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Criteo.Memcache.Configuration;
using Criteo.Memcache.Exceptions;
using Criteo.Memcache.Headers;
using Criteo.Memcache.Requests;

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

        private readonly EndPoint _endPoint;
        private readonly MemcacheClientConfiguration _clientConfig;

        private readonly Action<IMemcacheTransport> _registerEvents;
        private readonly Action<IMemcacheTransport> _transportAvailable;
        private Action<IMemcacheTransport> _sendComplete;
        private readonly Func<bool> _nodeClosing;

        private readonly Timer _connectTimer;

        private volatile bool _disposed = false;
        private volatile bool _initialized = false;
        private volatile bool _alive;
        private int _ongoingShutdown = 0;          // Integer used as a boolean
        private int _transportAvailableInReceive = 0;
        private readonly ConcurrentQueue<IMemcacheRequest> _pendingRequests;
        private Socket _socket;

        private readonly SocketAsyncEventArgs _sendAsyncEvtArgs;
        private readonly SocketAsyncEventArgs _receiveHeaderAsyncEvtArgs;
        private readonly SocketAsyncEventArgs _receiveBodyAsyncEvtArgs;
        private readonly int _pinnedBufferSize;

        private MemcacheResponseHeader _currentResponse;

        // The Registered property is set to true by the memcache node
        // when it acknowledges the transport is a working transport.
        public bool Registered { get; set; }

        // Default transport allocator
        public static TransportAllocator DefaultAllocator =
            (endPoint, config, register, available, autoConnect, closingNode)
                => new MemcacheTransport(endPoint, config, register, available, autoConnect, closingNode);

        /// <summary>
        /// Ctor, intialize things ...
        /// </summary>
        /// <param name="endpoint" />
        /// <param name="clientConfig">The client configuration</param>
        /// <param name="registerEvents">Delegate to call to register the transport</param>
        /// <param name="transportAvailable">Delegate to call when the transport is alive</param>
        /// <param name="planToConnect">If true, connect in a timer handler started immediately and call transportAvailable.
        ///                             Otherwise, the transport will connect synchronously at the first request</param>
        /// <param name="nodeClosing">Interface to check if the node is being disposed of</param>
        public MemcacheTransport(EndPoint endpoint, MemcacheClientConfiguration clientConfig, Action<IMemcacheTransport> registerEvents, Action<IMemcacheTransport> transportAvailable, bool planToConnect, Func<bool> nodeClosing)
        {
            if (clientConfig == null)
                throw new ArgumentException("Client config should not be null");

            _endPoint = endpoint;
            _clientConfig = clientConfig;
            _registerEvents = registerEvents;
            _transportAvailable = transportAvailable;
            _nodeClosing = nodeClosing;
            _pinnedBufferSize = clientConfig.PinnedBufferSize;

            _connectTimer = new Timer(TryConnect, null, Timeout.Infinite, Timeout.Infinite);
            _initialized = false;

            _registerEvents(this);

            _pendingRequests = new ConcurrentQueue<IMemcacheRequest>();

            _sendAsyncEvtArgs = new SocketAsyncEventArgs();
            _sendAsyncEvtArgs.SetBuffer(new byte[_pinnedBufferSize], 0, _pinnedBufferSize);
            _sendAsyncEvtArgs.Completed += OnSendRequestComplete;

            _receiveHeaderAsyncEvtArgs = new SocketAsyncEventArgs();
            _receiveHeaderAsyncEvtArgs.SetBuffer(new byte[MemcacheResponseHeader.Size], 0, MemcacheResponseHeader.Size);
            _receiveHeaderAsyncEvtArgs.Completed += OnReceiveHeaderComplete;

            _receiveBodyAsyncEvtArgs = new SocketAsyncEventArgs();
            _receiveBodyAsyncEvtArgs.SetBuffer(new byte[_pinnedBufferSize], 0, _pinnedBufferSize);
            _receiveBodyAsyncEvtArgs.Completed += OnReceiveBodyComplete;

            if (planToConnect)
                _connectTimer.Change(0, Timeout.Infinite);
            else if (transportAvailable != null)
                transportAvailable(this);
            _alive = !planToConnect;
        }

        /// <summary>
        /// Synchronously sends a request
        /// </summary>
        /// <param name="request" />
        public bool TrySend(IMemcacheRequest request)
        {
            if (request == null || _disposed || _ongoingShutdown == 1)
                return false;

            if (!_initialized && !Initialize())
                return false;

            return SendRequest(request);
        }

        public void Shutdown(Action callback)
        {
            if (_disposed)
                return;

            // Ensure that only one thread triggers the QuitRequest
            if (0 == Interlocked.Exchange(ref _ongoingShutdown, 1)
                && _initialized
                && null != callback)
                SendRequest(new QuitRequest(() =>
                    {
                        callback();
                        Dispose();
                    }));

            if (null == callback)
            {
                FailPending();
                Dispose();
            }
        }

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
                lock (this)
                    if (!_disposed)
                    {
                        if (disposing)
                        {
                            if (_connectTimer != null)
                                _connectTimer.Dispose();

                            var socket = Interlocked.Exchange(ref _socket, null);
                            if (socket != null)
                                socket.Dispose();

                            if (_sendAsyncEvtArgs != null)
                                _sendAsyncEvtArgs.Dispose();

                            if (_receiveHeaderAsyncEvtArgs != null)
                                _receiveHeaderAsyncEvtArgs.Dispose();

                            if (_receiveBodyAsyncEvtArgs != null)
                                _receiveBodyAsyncEvtArgs.Dispose();
                        }
                        _disposed = true;
                    }
        }

        #endregion IDisposable

        private void CreateSocket()
        {
            var socket = new Socket(_endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(_endPoint);
            socket.NoDelay = true;

            socket.ReceiveBufferSize = _clientConfig.TransportReceiveBufferSize;
            socket.SendBufferSize = _clientConfig.TransportReceiveBufferSize;

            var oldSocket = Interlocked.Exchange(ref _socket, socket);
            if (oldSocket != null)
                oldSocket.Dispose();
        }

        private void FailPending()
        {
            IMemcacheRequest request;
            while (_pendingRequests.TryDequeue(out request))
            {
                try
                {
                    request.Fail();
                }
                catch (Exception e)
                {
                    if (TransportError != null)
                        TransportError(e);
                }
            }
        }

        private bool Initialize()
        {
            lock (this)
            {
                if (!_initialized && !_disposed)
                {
                    try
                    {
                        CreateSocket();
                    }
                    catch (Exception e)
                    {
                        // if create socket fails, then nothing is initialized, retry only the connection
                        if (TransportError != null)
                            TransportError(e);

                        if (!_disposed)
                            _connectTimer.Change((int)_clientConfig.TransportConnectTimerPeriod.TotalMilliseconds, Timeout.Infinite);

                        if (_alive && TransportDead != null)
                            TransportDead(this);

                        _alive = false;

                        return false;
                    }

                    try
                    {
                        Start();
                        _initialized = true;
                        _alive = true;
                        _sendComplete = _transportAvailable;
                    }
                    catch (Exception e)
                    {
                        // once the start method has been called, if a fail occurs,
                        // we must create everything from scratch since we are in an unknow state
                        TransportFailureOnSend(e);
                        return false;
                    }
                }
            }

            return true;
        }

        private IMemcacheRequest DequeueToMatch(MemcacheResponseHeader header)
        {
            if (header.Opcode.IsQuiet())
                throw new MemcacheException("No way we can match a quiet request ! Header: " + header);

            IMemcacheRequest result;

            // hacky case on partial response for stat command
            if (header.Opcode == Opcode.Stat && header.TotalBodyLength != 0 && header.Status == Status.NoError)
            {
                if (!_pendingRequests.TryPeek(out result))
                    throw new MemcacheException("Received a response when no request is pending (Opcode.Stat). Header: " + header);
            }
            else
            {
                if (!_pendingRequests.TryDequeue(out result))
                    throw new MemcacheException("Received a response when no request is pending. Header: " + header);
            }

            if (result.RequestId != header.Opaque)
            {
                try
                {
                    result.Fail();
                }
                catch (Exception e)
                {
                    if (TransportError != null)
                        TransportError(e);
                }
                throw new MemcacheException("Received a response that doesn't match with the sent request queue : sent " + result + " received " + header);
            }

            AvailableInReceive();
            return result;
        }

        #region Reads

        private void ReceiveResponse()
        {
            try
            {
                _receiveHeaderAsyncEvtArgs.SetBuffer(0, MemcacheResponseHeader.Size);
                if (!_socket.ReceiveAsync(_receiveHeaderAsyncEvtArgs))
                    OnReceiveHeaderComplete(_socket, _receiveHeaderAsyncEvtArgs);
            }
            catch (Exception e)
            {
                TransportFailureOnReceive(e);
            }
        }

        private void OnReceiveHeaderComplete(object sender, SocketAsyncEventArgs args)
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
                if (args.BytesTransferred + args.Offset < MemcacheResponseHeader.Size)
                {
                    int offset = args.BytesTransferred + args.Offset;
                    args.SetBuffer(offset, MemcacheResponseHeader.Size - offset);
                    if (!socket.ReceiveAsync(args))
                        OnReceiveHeaderComplete(socket, args);
                    return;
                }

                ReceiveBody(args.Buffer);
            }
            catch (Exception e)
            {
                TransportFailureOnReceive(e);
                // if the receive header failed, we must restack the transport cause only sends detects it
            }
        }

        #endregion Reads

        private void ReceiveBody(byte[] headerBytes)
        {
            _currentResponse = new MemcacheResponseHeader(headerBytes);

            long sizeToRead = Math.Min(_pinnedBufferSize, _currentResponse.TotalBodyLength);

            var bodyStream = _currentResponse.TotalBodyLength == 0 ? null : new MemoryStream((int)_currentResponse.TotalBodyLength);
            _receiveBodyAsyncEvtArgs.SetBuffer(0, (int)sizeToRead);
            _receiveBodyAsyncEvtArgs.UserToken = bodyStream;

            try
            {
                if (_currentResponse.TotalBodyLength == 0 || !_socket.ReceiveAsync(_receiveBodyAsyncEvtArgs))
                    OnReceiveBodyComplete(_socket, _receiveBodyAsyncEvtArgs);
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

                var bodyStream = args.UserToken as MemoryStream;
                byte[] receivedBuffer = null;
                if (bodyStream != null)
                {
                    bodyStream.Write(args.Buffer, 0, args.BytesTransferred);
                    // check if we read a full body, else continue
                    if (bodyStream.Position < _currentResponse.TotalBodyLength)
                    {
                        int sizeToRead = (int)Math.Min(_pinnedBufferSize, _currentResponse.TotalBodyLength - bodyStream.Position);
                        args.SetBuffer(0, sizeToRead);
                        if (!socket.ReceiveAsync(args))
                            OnReceiveHeaderComplete(socket, args);
                        return;
                    }

                    // the full buffer has been received, assign it
#if NET_CORE
                    ArraySegment<byte> arraySegment;
                    bodyStream.TryGetBuffer(out arraySegment);
                    receivedBuffer = arraySegment.Array;
#else
                    receivedBuffer = bodyStream.GetBuffer();
#endif
                    bodyStream.Dispose();
                }

                // should assert we have the good request
                var request = DequeueToMatch(_currentResponse);

                if (MemcacheResponse != null)
                    MemcacheResponse(_currentResponse, request);
                if (_currentResponse.Status != Status.NoError && MemcacheError != null)
                    MemcacheError(_currentResponse, request);

                byte[] extra = null;
                if (_currentResponse.ExtraLength == _currentResponse.TotalBodyLength)
                    extra = receivedBuffer;
                else if (_currentResponse.ExtraLength > 0)
                {
                    extra = new byte[_currentResponse.ExtraLength];
                    Array.Copy(receivedBuffer, 0, extra, 0, _currentResponse.ExtraLength);
                }

                byte[] key = null;
                if (_currentResponse.KeyLength > 0)
                {
                    key = new byte[_currentResponse.KeyLength];
                    Array.Copy(receivedBuffer, _currentResponse.ExtraLength, key, 0, _currentResponse.KeyLength);
                }

                var payloadLength = _currentResponse.TotalBodyLength - _currentResponse.KeyLength - _currentResponse.ExtraLength;
                byte[] payload = null;
                if (payloadLength == _currentResponse.TotalBodyLength)
                {
                    payload = receivedBuffer;
                }
                else if (payloadLength > 0)
                {
                    payload = new byte[payloadLength];
                    Array.Copy(receivedBuffer, _currentResponse.KeyLength + _currentResponse.ExtraLength, payload, 0, Convert.ToInt32(payloadLength));
                }

                if (request != null)
                {
                    try
                    {
                        request.HandleResponse(_currentResponse, key, extra, payload);
                    }
                    catch (Exception e)
                    {
                        if (TransportError != null)
                            TransportError(e);
                    }
                }

                // loop the read on the socket
                ReceiveResponse();
            }
            catch (Exception e)
            {
                TransportFailureOnReceive(e);
            }
        }

        private void AvailableInReceive()
        {
            if (1 == Interlocked.CompareExchange(ref _transportAvailableInReceive, 0, 1))
            {
                // the flag has been successfully reset
                SendComplete();
            }
        }

        private void TransportFailureOnReceive(Exception e)
        {
            if (!_disposed)
                lock (this)
                    if (!_disposed)
                    {
                        try
                        {
                            if (_socket != null)
                            {
                                _socket.Shutdown(SocketShutdown.Both);
                            }
                        }
                        catch (Exception ex)
                        {
                            if (TransportError != null)
                                TransportError(new MemcacheException("Exception disconnecting the socket on " + this, ex));
                        }

                        if (TransportError != null)
                            TransportError(new MemcacheException("TransportFailureOnReceive on " + this, e));
                    }

            FailPending();
            AvailableInReceive();
        }

        private void Start()
        {
            ReceiveResponse();
            Authenticate();
        }

        /// <summary>
        /// This method is able to handle multisteps authitications
        /// (even if it's not implemented here, SASL can have multi-steps authentications)
        /// </summary>
        private bool Authenticate()
        {
            if (_clientConfig.Authenticator == null)
                return true;

            using (var mre = new ManualResetEventSlim(true))
            {
                bool authDone = false;
                var authenticationToken = _clientConfig.Authenticator.CreateToken();

                // we need to synchronize the sends, else the authentication response could be triggerd before
                // the send is complete (bug already seen, don't remove that synchronization)
                _sendComplete = _ => mre.Set();
                while (authenticationToken != null && !authDone)
                {
                    IMemcacheRequest request;
                    // the StepAuthenticate is blocking, it will wait for the response before sending the next step
                    var authStatus = authenticationToken.StepAuthenticate(_clientConfig.SocketTimeout, out request);
                    switch (authStatus)
                    {
                        case Status.NoError:
                            // auth OK, clear the token
                            authenticationToken = null;
                            authDone = true;
                            break;

                        case Status.StepRequired:
                            if (request == null)
                                throw new AuthenticationException("Unable to authenticate : step required but no request from token");
                            mre.Reset();
                            if (!SendRequest(request))
                                throw new AuthenticationException("Unable to authenticate : unable to send authentication request");
                            mre.Wait();
                            break;

                        default:
                            throw new AuthenticationException("Unable to authenticate : status " + authStatus);
                    }
                }
            }

            return true;
        }

        private void TryConnect(object dummy)
        {
            // If the node is closing, dispose this transport, which will terminate the reconnect timer.
            // If we do not know if the node is closed (_nodeClosing == null) then we also dispose, to prevent leaks.
            if (_nodeClosing == null || _nodeClosing())
            {
                Dispose();
            }
            // Else, try to connect and register the transport on the node in case of success. If the connection
            // fails, the Initialize method reschedules it.
            else if (Initialize())
            {
                SendComplete();
            }
        }

        private bool SendAsync(byte[] buffer)
        {
            int sizeToSend = Math.Min(_pinnedBufferSize, buffer.Length);
            var sendStream = new MemoryStream(buffer);
            sendStream.Read(_sendAsyncEvtArgs.Buffer, 0, sizeToSend);
            // reset the position, since it will be used to track what's been actually read from the async call
            sendStream.Position = 0;
            _sendAsyncEvtArgs.SetBuffer(0, sizeToSend);
            _sendAsyncEvtArgs.UserToken = sendStream;
            if (!_socket.SendAsync(_sendAsyncEvtArgs))
            {
                OnSendRequestComplete(_socket, _sendAsyncEvtArgs);
                return false;
            }

            return true;
        }

        private void OnSendRequestComplete(object sender, SocketAsyncEventArgs args)
        {
            try
            {
                var socket = (Socket)sender;
                var sendStream = (MemoryStream)args.UserToken;

                if (args.SocketError != SocketError.Success)
                    throw new SocketException((int)args.SocketError);

                int byteSentToProcess = (int)sendStream.Position + args.Count;
                bool allReadFromStream = byteSentToProcess == sendStream.Length;

                // the stream has not been entierly processed or we haven't sent all prepared data
                if (!allReadFromStream || args.BytesTransferred < args.Count - args.Offset)
                {
                    // recompute the current position in the pinned buffer and the stream
                    int newBufferOffset = args.Offset + args.BytesTransferred;
                    // setup the stream position to what's been actually processed
                    sendStream.Position += args.BytesTransferred;

                    if (allReadFromStream || newBufferOffset < _pinnedBufferSize / 2)
                    {
                        // if the buffer already contains all the data or there is still more than half to consume
                        // just resend the current buffer
                        args.SetBuffer(newBufferOffset, args.Count - args.BytesTransferred);
                    }
                    else
                    {
                        // rewrite all we can to the pinned buffer
                        int sizeToSend = (int)Math.Min(sendStream.Length - sendStream.Position, _pinnedBufferSize);
                        sendStream.Read(args.Buffer, 0, sizeToSend);
                        // rewind the stream to keep the position to what's been actually sent
                        sendStream.Position -= sizeToSend;
                        args.SetBuffer(0, sizeToSend);
                    }
                    if (!socket.SendAsync(args))
                        OnSendRequestComplete(socket, args);
                    return;
                }

                sendStream.Dispose();
                SendComplete();
            }
            catch (Exception e)
            {
                TransportFailureOnSend(e);
            }
        }

        private bool SendRequest(IMemcacheRequest request)
        {
            try
            {
                var buffer = request.GetQueryBuffer();

                if (_clientConfig.QueueLength > 0 &&
                    _pendingRequests.Count >= _clientConfig.QueueLength)
                {
                    // The request queue is full, the transport will be put back in the pool after the queue is not full anymore
                    Interlocked.Exchange(ref _transportAvailableInReceive, 1);
                    if (!_pendingRequests.IsEmpty)
                        // the receive will reset the flag after the next dequeue
                        return false;

                    if (0 == Interlocked.CompareExchange(ref _transportAvailableInReceive, 0, 1))
                        // the flag has already been reset (by the receive)
                        return false;
                }

                _pendingRequests.Enqueue(request);
                SendAsync(buffer);
            }
            catch (Exception e)
            {
                TransportFailureOnSend(e);
                return false;
            }

            return true;
        }

        // Register the transport on the node.
        private void SendComplete()
        {
            if (_ongoingShutdown != 0)
                return;
            try
            {
                if (_sendComplete != null)
                    _sendComplete(this);
            }
            catch (Exception e)
            {
                if (TransportError != null)
                    TransportError(e);
            }
        }

        private void TransportFailureOnSend(Exception e)
        {
            if (_disposed)
                return;

            lock (this)
            {
                if (_disposed)
                    return;

                if (TransportError != null)
                    TransportError(new MemcacheException("TransportFailureOnSend on " + this, e));

                // If the node hasn't been disposed, allocate a new transport that will attempt to reconnect
                if (_nodeClosing != null && !_nodeClosing())
                {
                    // The transport constructor will add the new transport to the pool by calling the _transportAvailable delegate
                    var factory = _clientConfig.TransportFactory ?? DefaultAllocator;
                    factory(_endPoint, _clientConfig, _registerEvents, _transportAvailable, true, _nodeClosing);
                }

                // Shutdown and dispose this transport
                if (TransportDead != null)
                    TransportDead(this);

                Dispose();
            }

            FailPending();
        }

        public override string ToString()
        {
            return "MemcacheTransport " + _endPoint + " " + GetHashCode();
        }
    }
}
