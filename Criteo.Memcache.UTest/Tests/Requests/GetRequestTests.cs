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
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using NUnit.Framework;

using Criteo.Memcache.Headers;
using Criteo.Memcache.Requests;

namespace Criteo.Memcache.UTest.Tests
{
    /// <summary>
    /// Test around the GetRequest object
    /// </summary>
    [TestFixture]
    public class GetRequestTests
    {
        static readonly byte[] GET_QUERY =
        {
            0x80, 0x00, 0x00, 0x05,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x05,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x48, 0x65, 0x6c, 0x6c,
            0x6f,
        };

        static readonly byte[] GET_FLAG =
        {
            0xde, 0xad, 0xbe, 0xef,
        };

        static readonly byte[] GET_MESSAGE =
        {
            0x57, 0x6f, 0x72, 0x6c,
            0x64,
        };

        [Test]
        public void GetRequestTest()
        {
            byte[] message = null;
            var request = new GetRequest(CallBackPolicy.AnyOK)
            {
                Key = "Hello".Select(c => (byte)c).ToArray(),
                RequestId = 0,
                CallBack = (s, m) => message = m,
            };

            var queryBuffer = request.GetQueryBuffer();

            CollectionAssert.AreEqual(GET_QUERY, queryBuffer, "The get query buffer is different from the expected one");

            var header = new MemcacheResponseHeader { Opcode = Opcode.Get, Status = Status.NoError };
            Assert.DoesNotThrow(() => request.HandleResponse(header, null, GET_FLAG, GET_MESSAGE), "Handle request should not throw an exception");

            Assert.AreSame(GET_MESSAGE, message, "Sent message and the one returned by the request are different");
        }

        [Test]
        public void GetRequestFailTest()
        {
            Status status = Status.UnknownCommand;
            byte[] message = new byte[0];

            var request = new GetRequest(CallBackPolicy.AnyOK)
            {
                Key = "Hello".Select(c => (byte)c).ToArray(),
                RequestId = 0,
                CallBack = (s, m) => { message = m; status = s; },
            };

            var queryBuffer = request.GetQueryBuffer();

            CollectionAssert.AreEqual(GET_QUERY, queryBuffer, "The get query buffer is different from the expected one");

            Assert.DoesNotThrow(request.Fail, "Fail should not throw an exception");

            Assert.AreEqual(Status.InternalError, status, "Returned status should be InternalError after a fail");
            Assert.IsNull(message, "Returned message should be a null reference after a fail");
        }

        [Test]
        public void RedundantGetRequestTest()
        {
            byte[] message = null;
            Status status = Status.UnknownCommand;

            var headerOK = new MemcacheResponseHeader { Opcode = Opcode.Get, Status = Status.NoError };
            var headerFail = new MemcacheResponseHeader { Opcode = Opcode.Get, Status = Status.KeyNotFound };

            // 1. Test redundancy = 3 and all gets are successful

            var request = new GetRequest(CallBackPolicy.AnyOK)
            {
                Key = "Hello".Select(c => (byte)c).ToArray(),
                RequestId = 0,
                CallBack = (s, m) => { message = m; status = s; },
                Replicas = 2,
            };

            var queryBuffer = request.GetQueryBuffer();
            CollectionAssert.AreEqual(GET_QUERY, queryBuffer, "The get query buffer is different from the expected one");

            Assert.DoesNotThrow(() => request.HandleResponse(headerOK, null, GET_FLAG, GET_MESSAGE), "Handle request should not throw an exception");
            Assert.AreEqual(Status.NoError, status, "Returned status should be NoError after the first successful get");
            Assert.AreSame(GET_MESSAGE, message, "Sent message and the one returned by the request are different");

            status = Status.UnknownCommand;
            Assert.DoesNotThrow(() => request.HandleResponse(headerOK, null, GET_FLAG, GET_MESSAGE), "Handle request should not throw an exception");
            Assert.AreEqual(Status.UnknownCommand, status, "The callback should not be called a second time");

            status = Status.UnknownCommand;
            Assert.DoesNotThrow(() => request.HandleResponse(headerOK, null, GET_FLAG, GET_MESSAGE), "Handle request should not throw an exception");
            Assert.AreEqual(Status.UnknownCommand, status, "The callback should not be called a second time");

            // 2. Test redundancy = 3, the first get is failing

            request = new GetRequest(CallBackPolicy.AnyOK)
            {
                Key = "Hello".Select(c => (byte)c).ToArray(),
                RequestId = 0,
                CallBack = (s, m) => { message = m; status = s; },
                Replicas = 2,
            };
            status = Status.UnknownCommand;

            Assert.DoesNotThrow(() => request.HandleResponse(headerFail, null, GET_FLAG, GET_MESSAGE), "Handle request should not throw an exception");
            Assert.AreEqual(Status.UnknownCommand, status, "Callback should not be called after the first failed get");

            Assert.DoesNotThrow(() => request.HandleResponse(headerOK, null, GET_FLAG, GET_MESSAGE), "Handle request should not throw an exception");
            Assert.AreEqual(Status.NoError, status, "Returned status should be NoError after the first successful get");
            Assert.AreSame(GET_MESSAGE, message, "Sent message and the one returned by the request are different");

            status = Status.UnknownCommand;
            Assert.DoesNotThrow(() => request.HandleResponse(headerOK, null, GET_FLAG, GET_MESSAGE), "Handle request should not throw an exception");
            Assert.AreEqual(Status.UnknownCommand, status, "The callback should not be called a second time");

            // 3. Test redundancy = 3, the first and second gets are failing

            request = new GetRequest(CallBackPolicy.AnyOK)
            {
                Key = "Hello".Select(c => (byte)c).ToArray(),
                RequestId = 0,
                CallBack = (s, m) => { message = m; status = s; },
                Replicas = 2,
            };
            status = Status.UnknownCommand;

            Assert.DoesNotThrow(() => request.HandleResponse(headerFail, null, GET_FLAG, GET_MESSAGE), "Handle request should not throw an exception");
            Assert.AreEqual(Status.UnknownCommand, status, "Callback should not be called after the first failed get");

            Assert.DoesNotThrow(() => request.Fail(), "Handle request should not throw an exception");
            Assert.AreEqual(Status.UnknownCommand, status, "Callback should not be called after the second failed get");

            Assert.DoesNotThrow(() => request.HandleResponse(headerOK, null, GET_FLAG, GET_MESSAGE), "Handle request should not throw an exception");
            Assert.AreEqual(Status.NoError, status, "Returned status should be NoError after the first successful get");
            Assert.AreSame(GET_MESSAGE, message, "Sent message and the one returned by the request are different");

        }

        [Test]
        public void RedundantGetRequestFailTest()
        {
            Status status = Status.UnknownCommand;

            var headerFail = new MemcacheResponseHeader { Opcode = Opcode.Get, Status = Status.KeyNotFound };

            // 1. Test redundancy = 3 and all gets are failing, the last on InternalError

            var request = new GetRequest(CallBackPolicy.AnyOK)
            {
                Key = "Hello".Select(c => (byte)c).ToArray(),
                RequestId = 0,
                CallBack = (s, m) => status = s,
                Replicas = 2,
            };

            var queryBuffer = request.GetQueryBuffer();
            CollectionAssert.AreEqual(GET_QUERY, queryBuffer, "The get query buffer is different from the expected one");

            Assert.DoesNotThrow(() => request.HandleResponse(headerFail, null, GET_FLAG, GET_MESSAGE), "Handle request should not throw an exception");
            Assert.AreEqual(Status.UnknownCommand, status, "Callback should not be called after the first failed get");

            Assert.DoesNotThrow(() => request.HandleResponse(headerFail, null, GET_FLAG, GET_MESSAGE), "Handle request should not throw an exception");
            Assert.AreEqual(Status.UnknownCommand, status, "Callback should not be called after the second failed get");

            Assert.DoesNotThrow(() => request.Fail(), "Handle request should not throw an exception");
            Assert.AreEqual(Status.KeyNotFound, status, "Returned status should be KeyNotFound");

            // 2. Test redundancy = 3 and all gets are failing, the last on KeyNotFound

            request = new GetRequest(CallBackPolicy.AnyOK)
            {
                Key = "Hello".Select(c => (byte)c).ToArray(),
                RequestId = 0,
                CallBack = (s, m) => status = s,
                Replicas = 2,
            };
            status = Status.UnknownCommand;

            Assert.DoesNotThrow(() => request.Fail(), "Handle request should not throw an exception");
            Assert.AreEqual(Status.UnknownCommand, status, "Callback should not be called after the first failed get");

            Assert.DoesNotThrow(() => request.Fail(), "Handle request should not throw an exception");
            Assert.AreEqual(Status.UnknownCommand, status, "Callback should not be called after the second failed get");

            Assert.DoesNotThrow(() => request.HandleResponse(headerFail, null, GET_FLAG, GET_MESSAGE), "Handle request should not throw an exception");
            Assert.AreEqual(Status.KeyNotFound, status, "Returned status should be KeyNotFound");

        }

        [Test]
        public void GetRequestValidInvalidTest()
        {
            // Invalid Expire times
            Assert.Throws<ArgumentException>(() => new GetRequest(CallBackPolicy.AnyOK) { Expire = TimeSpan.MinValue }, "Invalid negative expire time");
            Assert.Throws<ArgumentException>(() => new GetRequest(CallBackPolicy.AnyOK) { Expire = TimeSpan.FromSeconds(-1) }, "Invalid negative expire time");

            // Valid requests
            Assert.DoesNotThrow(() => new GetRequest(CallBackPolicy.AnyOK) { Expire = TimeSpan.Zero }, "Timestamp.Zero translates to infinite TTL");
            Assert.DoesNotThrow(() => new GetRequest(CallBackPolicy.AnyOK) { Expire = ExpirationTimeUtils.Infinite }, "Timestamp.Zero translates to infinite TTL");
            Assert.DoesNotThrow(() => new GetRequest(CallBackPolicy.AnyOK) { Expire = TimeSpan.MaxValue }, "Timestamp.MaxValue is considered valid, but the conversion to a timestamp will crash");
        }

        //AnyOK
        [TestCase(new Status[] {Status.NoError},
            CallBackPolicy.AnyOK, Status.NoError)]

        [TestCase(new Status[] { Status.NoError, Status.InternalError },
            CallBackPolicy.AnyOK, Status.NoError)]

        [TestCase(new Status[] { Status.KeyNotFound, Status.InternalError },
            CallBackPolicy.AnyOK, Status.KeyNotFound)] // if we contacted at least one node in cluster we should think cluster is OK

        [TestCase(new Status[] { Status.InternalError, Status.KeyNotFound },
            CallBackPolicy.AnyOK, Status.KeyNotFound)] // order doesn't matter

        [TestCase(new Status[] { Status.InternalError, Status.InternalError },
            CallBackPolicy.AnyOK, Status.InternalError)]

        [TestCase(new Status[] { Status.NoError, Status.KeyNotFound},
            CallBackPolicy.AnyOK, Status.NoError)]

        [TestCase(new Status[] { Status.NoError, Status.KeyNotFound, Status.Busy },
            CallBackPolicy.AnyOK, Status.NoError)]

        [TestCase(new Status[] { Status.KeyNotFound, Status.Busy , Status.NoError},
            CallBackPolicy.AnyOK, Status.NoError)] //order shouldn't matter

        [TestCase(new Status[] { Status.KeyNotFound },
            CallBackPolicy.AnyOK, Status.KeyNotFound)]

        [TestCase(new Status[] { Status.KeyNotFound , Status.KeyNotFound },
            CallBackPolicy.AnyOK, Status.KeyNotFound)]

        [TestCase(new Status[] { Status.KeyNotFound, Status.Busy },
            CallBackPolicy.AnyOK, Status.Busy)]

        [TestCase(new Status[] { Status.AuthRequired },
            CallBackPolicy.AnyOK, Status.AuthRequired)]

        // AllOK
        [TestCase(new Status[] { Status.NoError },
            CallBackPolicy.AllOK, Status.NoError)]

        [TestCase(new Status[] { Status.NoError, Status.NoError },
            CallBackPolicy.AllOK, Status.NoError)]

        [TestCase(new Status[] { Status.NoError, Status.KeyNotFound, Status.Busy },
            CallBackPolicy.AllOK, Status.KeyNotFound)]

        [TestCase(new Status[] { Status.KeyNotFound, Status.Busy, Status.NoError },
            CallBackPolicy.AllOK, Status.KeyNotFound)]

        [TestCase(new Status[] { Status.InternalError },
            CallBackPolicy.AllOK, Status.InternalError)]

        [TestCase(new Status[] { Status.InternalError, Status.NoError },
            CallBackPolicy.AllOK, Status.InternalError)]
        public void ResultAggregationTest(Status[] statuses, CallBackPolicy callBackPolicy, Status expectedResult)
        {
            Status result = Status.UnknownCommand;

            var request = new GetRequest(callBackPolicy)
            {
                Key = "Hello".Select(c => (byte) c).ToArray(),
                RequestId = 0,
                CallBack = (s, m) => result = s,
                Replicas = statuses.Length - 1,
            };
            foreach (var status in statuses)
                request.HandleResponse(new MemcacheResponseHeader { Opcode = Opcode.Get, Status = status }, null, GET_FLAG, GET_MESSAGE);
            Assert.AreEqual(expectedResult, result, string.Format("Input statuses: [{0}]", string.Join(", ", statuses)));
        }
    }
}
