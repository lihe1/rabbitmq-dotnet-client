// This source code is dual-licensed under the Apache License, version
// 2.0, and the Mozilla Public License, version 1.1.
//
// The APL v2.0:
//
//---------------------------------------------------------------------------
//   Copyright (c) 2007-2020 VMware, Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//       https://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//---------------------------------------------------------------------------
//
// The MPL v1.1:
//
//---------------------------------------------------------------------------
//  The contents of this file are subject to the Mozilla Public License
//  Version 1.1 (the "License"); you may not use this file except in
//  compliance with the License. You may obtain a copy of the License
//  at https://www.mozilla.org/MPL/
//
//  Software distributed under the License is distributed on an "AS IS"
//  basis, WITHOUT WARRANTY OF ANY KIND, either express or implied. See
//  the License for the specific language governing rights and
//  limitations under the License.
//
//  The Original Code is RabbitMQ.
//
//  The Initial Developer of the Original Code is Pivotal Software, Inc.
//  Copyright (c) 2007-2020 VMware, Inc.  All rights reserved.
//---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

using RabbitMQ.Client.Exceptions;
using RabbitMQ.Util;

namespace RabbitMQ.Client.Impl
{
    static class TaskExtensions
    {
        public static Task CompletedTask = Task.FromResult(0);

        public static async Task TimeoutAfter(this Task task, TimeSpan timeout)
        {
            if (task == await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false))
                await task;
            else
            {
                Task supressErrorTask = task.ContinueWith(t => t.Exception.Handle(e => true), TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException();
            }
        }
    }

    class SocketFrameHandler : IFrameHandler
    {
        // Socket poll timeout in ms. If the socket does not
        // become writeable in this amount of time, we throw
        // an exception.
        private TimeSpan _writeableStateTimeout = TimeSpan.FromSeconds(30);
        private int _writeableStateTimeoutMicroSeconds;
        private readonly NetworkBinaryReader _reader;
        private readonly ITcpClient _socket;
        private readonly NetworkBinaryWriter _writer;
        private readonly object _semaphore = new object();
        private readonly object _streamLock = new object();
        private bool _closed;
        public SocketFrameHandler(AmqpTcpEndpoint endpoint,
            Func<AddressFamily, ITcpClient> socketFactory,
            TimeSpan connectionTimeout, TimeSpan readTimeout, TimeSpan writeTimeout)
        {
            Endpoint = endpoint;

            if (ShouldTryIPv6(endpoint))
            {
                try
                {
                    _socket = ConnectUsingIPv6(endpoint, socketFactory, connectionTimeout);
                }
                catch (ConnectFailureException)
                {
                    _socket = null;
                }
            }

            if (_socket == null && endpoint.AddressFamily != AddressFamily.InterNetworkV6)
            {
                _socket = ConnectUsingIPv4(endpoint, socketFactory, connectionTimeout);
            }

            Stream netstream = _socket.GetStream();
            netstream.ReadTimeout = (int)readTimeout.TotalMilliseconds;
            netstream.WriteTimeout = (int)writeTimeout.TotalMilliseconds;

            if (endpoint.Ssl.Enabled)
            {
                try
                {
                    netstream = SslHelper.TcpUpgrade(netstream, endpoint.Ssl);
                }
                catch (Exception)
                {
                    Close();
                    throw;
                }
            }
            _reader = new NetworkBinaryReader(new BufferedStream(netstream, _socket.Client.ReceiveBufferSize));
            _writer = new NetworkBinaryWriter(netstream);

            WriteTimeout = writeTimeout;
        }
        public AmqpTcpEndpoint Endpoint { get; set; }

        public EndPoint LocalEndPoint
        {
            get { return _socket.Client.LocalEndPoint; }
        }

        public int LocalPort
        {
            get { return ((IPEndPoint)LocalEndPoint).Port; }
        }

        public EndPoint RemoteEndPoint
        {
            get { return _socket.Client.RemoteEndPoint; }
        }

        public int RemotePort
        {
            get { return ((IPEndPoint)LocalEndPoint).Port; }
        }

        public TimeSpan ReadTimeout
        {
            set
            {
                try
                {
                    if (_socket.Connected)
                    {
                        _socket.ReceiveTimeout = value;
                    }
                }
                catch (SocketException)
                {
                    // means that the socket is already closed
                }
            }
        }

        public TimeSpan WriteTimeout
        {
            set
            {
                _writeableStateTimeout = value;
                _socket.Client.SendTimeout = (int)_writeableStateTimeout.TotalMilliseconds;
                _writeableStateTimeoutMicroSeconds = _socket.Client.SendTimeout * 1000;
            }
        }

        public void Close()
        {
            lock (_semaphore)
            {
                if (!_closed)
                {
                    try
                    {
                        _socket.Close();
                    }
                    catch (Exception)
                    {
                        // ignore, we are closing anyway
                    }
                    finally
                    {
                        _closed = true;
                    }
                }
            }
        }

        public InboundFrame ReadFrame()
        {
            return RabbitMQ.Client.Impl.InboundFrame.ReadFrom(_reader);
        }

        private static readonly byte[] s_amqp = Encoding.ASCII.GetBytes("AMQP");
        public void SendHeader()
        {
            var ms = new MemoryStream();
            var nbw = new NetworkBinaryWriter(ms);
            nbw.Write(s_amqp);
            byte one = (byte)1;
            if (Endpoint.Protocol.Revision != 0)
            {
                nbw.Write((byte)0);
                nbw.Write((byte)Endpoint.Protocol.MajorVersion);
                nbw.Write((byte)Endpoint.Protocol.MinorVersion);
                nbw.Write((byte)Endpoint.Protocol.Revision);
            }
            else
            {
                nbw.Write(one);
                nbw.Write(one);
                nbw.Write((byte)Endpoint.Protocol.MajorVersion);
                nbw.Write((byte)Endpoint.Protocol.MinorVersion);
            }
            Write(ms.GetBufferSegment());
        }

        public void WriteFrame(OutboundFrame frame)
        {
            var ms = new MemoryStream();
            var nbw = new NetworkBinaryWriter(ms);
            frame.WriteTo(nbw);
            _socket.Client.Poll(_writeableStateTimeoutMicroSeconds, SelectMode.SelectWrite);
            Write(ms.GetBufferSegment());
        }

        public void WriteFrameSet(IList<OutboundFrame> frames)
        {
            var ms = new MemoryStream();
            var nbw = new NetworkBinaryWriter(ms);
            for (int i = 0; i < frames.Count; ++i)
            {
                frames[i].WriteTo(nbw);
            }
            _socket.Client.Poll(_writeableStateTimeoutMicroSeconds, SelectMode.SelectWrite);
            Write(ms.GetBufferSegment());
        }

        private void Write(ArraySegment<byte> bufferSegment)
        {
            lock (_streamLock)
            {
                _writer.Write(bufferSegment.Array, bufferSegment.Offset, bufferSegment.Count);
            }
        }

        private bool ShouldTryIPv6(AmqpTcpEndpoint endpoint)
        {
            return Socket.OSSupportsIPv6 && endpoint.AddressFamily != AddressFamily.InterNetwork;
        }

        private ITcpClient ConnectUsingIPv6(AmqpTcpEndpoint endpoint,
                                            Func<AddressFamily, ITcpClient> socketFactory,
                                            TimeSpan timeout)
        {
            return ConnectUsingAddressFamily(endpoint, socketFactory, timeout, AddressFamily.InterNetworkV6);
        }

        private ITcpClient ConnectUsingIPv4(AmqpTcpEndpoint endpoint,
                                            Func<AddressFamily, ITcpClient> socketFactory,
                                            TimeSpan timeout)
        {
            return ConnectUsingAddressFamily(endpoint, socketFactory, timeout, AddressFamily.InterNetwork);
        }

        private ITcpClient ConnectUsingAddressFamily(AmqpTcpEndpoint endpoint,
                                                    Func<AddressFamily, ITcpClient> socketFactory,
                                                    TimeSpan timeout, AddressFamily family)
        {
            ITcpClient socket = socketFactory(family);
            try
            {
                ConnectOrFail(socket, endpoint, timeout);
                return socket;
            }
            catch (ConnectFailureException e)
            {
                socket.Dispose();
                throw e;
            }
        }

        private void ConnectOrFail(ITcpClient socket, AmqpTcpEndpoint endpoint, TimeSpan timeout)
        {
            try
            {
                socket.ConnectAsync(endpoint.HostName, endpoint.Port)
                     .TimeoutAfter(timeout)
                     .ConfigureAwait(false)
                     // this ensures exceptions aren't wrapped in an AggregateException
                     .GetAwaiter()
                     .GetResult();
            }
            catch (ArgumentException e)
            {
                throw new ConnectFailureException("Connection failed", e);
            }
            catch (SocketException e)
            {
                throw new ConnectFailureException("Connection failed", e);
            }
            catch (NotSupportedException e)
            {
                throw new ConnectFailureException("Connection failed", e);
            }
            catch (TimeoutException e)
            {
                throw new ConnectFailureException("Connection failed", e);
            }
        }
    }
}
