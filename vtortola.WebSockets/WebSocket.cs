﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace vtortola.WebSockets
{
    public sealed class WebSocket : IDisposable
    {
        readonly SemaphoreSlim _writeSemaphore;
        readonly Stream _clientStream;
        readonly WebSocketListenerOptions _options;
        Int32 _gracefullyClosed, _closed, _disposed;
        readonly TimeSpan _pingInterval, _pingTimeout;
        DateTime _lastPong;
        readonly IReadOnlyList<IWebSocketMessageExtensionContext> _extensions;
        Int32 _ongoingMessageWrite;
        Int32 _ongoingMessageAwaiting;

        public IPEndPoint RemoteEndpoint { get; private set; }
        public IPEndPoint LocalEndpoint { get; private set; }
        public Boolean IsConnected { get { return _closed != 1; } }
        public WebSocketHttpRequest HttpRequest { get; private set; }
        internal WebSocketFrameHeader Header { get; private set; }
        internal readonly Byte[] WriteBufferTail;
                
        public WebSocket(Stream clientStream, IPEndPoint local, IPEndPoint remote, WebSocketHttpRequest httpRequest, WebSocketListenerOptions options, IReadOnlyList<IWebSocketMessageExtensionContext> extensions)
        {
            if (clientStream == null)
                throw new ArgumentNullException("clientStream");

            if (httpRequest == null)
                throw new ArgumentNullException("httpRequest");

            if (options == null)
                throw new ArgumentNullException("options");

            _writeSemaphore = new SemaphoreSlim(1);
            _options = options;
            RemoteEndpoint = remote;
            LocalEndpoint = local;
            HttpRequest = httpRequest;
            _extensions = extensions;
            _clientStream = clientStream;
            WriteBufferTail = new Byte[_options.SendBufferSize];

            if (options.PingTimeout != Timeout.InfiniteTimeSpan)
            {
                _pingTimeout = options.PingTimeout;
                _lastPong = DateTime.Now.Add(_pingTimeout);
                _pingInterval = TimeSpan.FromMilliseconds(Math.Min(5000, options.PingTimeout.TotalMilliseconds / 2));

                Task.Run((Func<Task>)PingAsync);
            }
        }
                
        public async Task<WebSocketMessageReadStream> ReadMessageAsync(CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref _ongoingMessageAwaiting, 1, 0) == 1)
                throw new WebSocketException("There is an ongoing message await from somewhere else. Only a single write is allowed at the time.");

            if (Header != null && Header.OngoingMessageRead)
                throw new WebSocketException("There is an ongoing message that is being readed from somewhere else");

            if(Header==null)
                await AwaitHeaderAsync(token);

            _ongoingMessageAwaiting = 0;

            if (this.IsConnected && Header != null)
            {
                Header.OngoingMessageRead = true;
                WebSocketMessageReadStream reader = new WebSocketMessageReadNetworkStream(this, Header);
                foreach (var extension in _extensions)
                    reader = extension.ExtendReader(reader);
                return reader;
            }

            return null;
        }

        public WebSocketMessageReadStream ReadMessage()
        {
            if (Interlocked.CompareExchange(ref _ongoingMessageAwaiting, 1, 0) == 1)
                throw new WebSocketException("There is an ongoing message await from somewhere else. Only a single write is allowed at the time.");

            if (Header != null && Header.OngoingMessageRead)
                throw new WebSocketException("There is an ongoing message that is being readed from somewhere else. Ony a single read is allowed at the time.");

            if (Header == null)
                AwaitHeader();

            _ongoingMessageAwaiting = 0;

            if (this.IsConnected && Header != null)
            {
                Header.OngoingMessageRead = true;
                WebSocketMessageReadStream reader = new WebSocketMessageReadNetworkStream(this, Header);
                foreach (var extension in _extensions)
                    reader = extension.ExtendReader(reader);
                return reader;
            }

            return null;
        }

        public WebSocketMessageWriteStream CreateMessageWriter(WebSocketMessageType messageType)
        {
            if(Interlocked.CompareExchange(ref _ongoingMessageWrite, 1, 0) == 1)
                throw new WebSocketException("There is an ongoing message that is being written from somewhere else. Only a single write is allowed at the time.");
            
            WebSocketMessageWriteStream writer = new WebSocketMessageWriteNetworkStream(this, messageType);

            foreach (var extension in _extensions)
                writer = extension.ExtendWriter(writer);

            return writer;
        }

        internal void WritingEnd()
        {
            _ongoingMessageWrite = 0;
        }

        readonly Byte[] _headerBuffer = new Byte[14];
        private WebSocketFrameHeader ParseHeader(ref Int32 readed)
        {
            WebSocketFrameHeader header;
            if(!TryReadHeaderUntil(ref readed, 6))
            {
                Close(WebSocketCloseReasons.ProtocolError);
                return null;
            }
            
            Int32 headerlength = WebSocketFrameHeader.GetHeaderLength(_headerBuffer, 0);

            if (!TryReadHeaderUntil(ref readed, headerlength))
            {
                Close(WebSocketCloseReasons.ProtocolError);
                return null;
            }

            if (!WebSocketFrameHeader.TryParse(_headerBuffer, 0, headerlength, out header))
                throw new WebSocketException("Cannot understand frame header");
            else
                return header;
        }

        private Boolean TryReadHeaderUntil(ref Int32 readed, Int32 until)
        {
            Int32 r = 0;
            while (readed < until)
            {
                r = _clientStream.Read(_headerBuffer, readed, until - readed);
                if (r == 0)
                    return false;

                readed += r;
            }

            return true;
        }

        internal void AwaitHeader()
        {
            try
            {
                while (this.IsConnected && Header == null)
                {
                    // try read minimal frame
                    Int32 readed =  _clientStream.Read(_headerBuffer, 0, 6);
                    if (readed == 0 )
                    {
                        Close(WebSocketCloseReasons.ProtocolError);
                        return;
                    }

                    Header = ParseHeader(ref readed);
                    if (Header == null)
                    {
                        Close(WebSocketCloseReasons.ProtocolError);
                        return;
                    }

                    if (!Header.Flags.Option.IsData())
                    {
                        ProcessControlFrame(_clientStream);
                        readed = 0;
                        Header = null;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                Close(WebSocketCloseReasons.ProtocolError);
            }
            catch (IOException)
            {
                Close(WebSocketCloseReasons.ProtocolError);
            }
        }  
        internal async Task AwaitHeaderAsync(CancellationToken cancellation)
        {
            try
            {
                while (this.IsConnected && Header == null)
                {
                    // try read minimal frame
                    Int32 readed = await _clientStream.ReadAsync(_headerBuffer, 0, 6, cancellation);
                    if (readed == 0 || cancellation.IsCancellationRequested)
                    {
                        Close(WebSocketCloseReasons.ProtocolError);
                        return;
                    }

                    Header = ParseHeader(ref readed);
                    if(Header == null || cancellation.IsCancellationRequested)
                    {
                        Close(WebSocketCloseReasons.ProtocolError);
                        return;
                    }

                    if (!Header.Flags.Option.IsData())
                    {
                        ProcessControlFrame(_clientStream);
                        readed = 0;
                        Header = null;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                Close(WebSocketCloseReasons.ProtocolError);
            }
            catch(IOException)
            {
                Close(WebSocketCloseReasons.ProtocolError);
            }
        }        
        internal void CleanHeader()
        {
            Header = null;
        }
        internal async Task<Int32> ReadInternalAsync(Byte[] buffer, Int32 offset, Int32 count, CancellationToken cancellationToken)
        {
            try
            {
                var readed = await _clientStream.ReadAsync(buffer, offset, count, cancellationToken);

                Header.DecodeBytes(buffer, offset, readed);

                return readed;
            }
            catch (InvalidOperationException)
            {
                this.Close(WebSocketCloseReasons.UnexpectedCondition);
                return 0;
            }
            catch (IOException)
            {
                this.Close(WebSocketCloseReasons.UnexpectedCondition);
                return 0;
            }
        }     
        internal Int32 ReadInternal(Byte[] buffer, Int32 offset, Int32 count)
        {
            try
            {
                var readed = _clientStream.Read(buffer, offset, count);

                Header.DecodeBytes(buffer, offset, readed);

                return readed;
            }
            catch (InvalidOperationException)
            {
                this.Close(WebSocketCloseReasons.UnexpectedCondition);
                return 0;
            }
            catch (IOException)
            {
                this.Close(WebSocketCloseReasons.UnexpectedCondition);
                return 0;
            }
        }

        readonly Byte[] _controlFrameBuffer = new Byte[125];
        private void ProcessControlFrame(Stream clientStream)
        {
            switch (Header.Flags.Option)
            {
                case WebSocketFrameOption.Continuation:
                case WebSocketFrameOption.Text:
                case WebSocketFrameOption.Binary:
                    throw new WebSocketException("Text, Continuation or Binary are not protocol frames");

                case WebSocketFrameOption.ConnectionClose:
                    this.Close(WebSocketCloseReasons.NormalClose);
                    break;

                case WebSocketFrameOption.Ping:
                    break;

                case WebSocketFrameOption.Pong:
                    Int32 contentLength =  _controlFrameBuffer.Length;
                    if (Header.ContentLength < 125)
                        contentLength = (Int32)Header.ContentLength;
                    Int32 readed = 0;
                    while (Header.RemainingBytes > 0)
                    {
                        readed = clientStream.Read(_controlFrameBuffer, readed, contentLength - readed);
                        Header.DecodeBytes(_controlFrameBuffer, 0, readed);
                    }
                    _lastPong = DateTime.Now;
                    break;
                default: throw new WebSocketException("Unexpected header option '" + Header.Flags.Option.ToString() + "'");
            }
        }
        readonly Byte[] _pingBuffer = new Byte[2];
        private async Task PingAsync()
        {
            while (this.IsConnected)
            {
                await Task.Delay(_pingInterval);

                try
                {
                    var now = DateTime.Now;

                    if (_lastPong.Add(_pingTimeout).Add(_pingInterval) < now)
                        Close(WebSocketCloseReasons.NormalClose);
                    else
                        await this.WriteInternalAsync(_pingBuffer, 0, 0, true, false, WebSocketFrameOption.Ping, WebSocketExtensionFlags.None, CancellationToken.None);
                }
                catch{}
            }
        }
        internal void WriteInternal(Byte[] buffer, Int32 offset, Int32 count, Boolean isCompleted, Boolean headerSent, WebSocketFrameOption option, WebSocketExtensionFlags extensionFlags)
        {
            try
            {
                var header = WebSocketFrameHeader.Create(count, isCompleted, headerSent, option, extensionFlags);

                if (buffer.Length >= offset + count + header.HeaderLength)
                {
                    buffer.ShiftRight(header.HeaderLength + offset, count);
                    Array.Copy(header.Raw, 0, buffer, offset, header.HeaderLength);

                    if (!_writeSemaphore.Wait(_options.WebSocketSendTimeout))
                        throw new WebSocketException("Write timeout");
                    _clientStream.Write(buffer, offset, count + header.HeaderLength);
                }
                else
                {
                    if (!_writeSemaphore.Wait(_options.WebSocketSendTimeout))
                        throw new WebSocketException("Write timeout");
                    _clientStream.Write(header.Raw, 0, header.HeaderLength);
                    _clientStream.Write(buffer, offset, count);
                }
            }
            catch (InvalidOperationException)
            {
                Close(WebSocketCloseReasons.UnexpectedCondition);
            }
            catch (IOException)
            {
                Close(WebSocketCloseReasons.UnexpectedCondition);
            }
            catch(Exception ex)
            {
                Close(WebSocketCloseReasons.UnexpectedCondition);
                throw new WebSocketException("Cannot write on WebSocket", ex);
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }
        internal async Task WriteInternalAsync(Byte[] buffer, Int32 offset, Int32 count, Boolean isCompleted, Boolean headerSent, WebSocketFrameOption option, WebSocketExtensionFlags extensionFlags, CancellationToken cancellation)
        {
            try
            {
               var header = WebSocketFrameHeader.Create(count, isCompleted, headerSent, option, extensionFlags);

                if (buffer.Length >= offset + count + header.HeaderLength)
                {
                    buffer.ShiftRight(header.HeaderLength + offset, count);
                    Array.Copy(header.Raw, 0, buffer, offset, header.HeaderLength);

                    if (!await _writeSemaphore.WaitAsync(_options.WebSocketSendTimeout, cancellation))
                        throw new WebSocketException("Write timeout");
                    await _clientStream.WriteAsync(buffer, offset, count + header.HeaderLength, cancellation);
                }
                else
                {
                    if (!await _writeSemaphore.WaitAsync(_options.WebSocketSendTimeout, cancellation))
                        throw new WebSocketException("Write timeout");
                    await _clientStream.WriteAsync(header.Raw, 0, header.HeaderLength);
                    await _clientStream.WriteAsync(buffer, offset, count);
                }
            }
            catch (InvalidOperationException)
            {
                Close(WebSocketCloseReasons.UnexpectedCondition);
            }
            catch (IOException)
            {
                Close(WebSocketCloseReasons.UnexpectedCondition);
            }
            catch (Exception ex)
            {
                Close(WebSocketCloseReasons.UnexpectedCondition);
                throw new WebSocketException("Cannot write on WebSocket",ex);
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }

        public void Close(WebSocketCloseReasons reason)
        {
            try
            {
                if (Interlocked.CompareExchange(ref _closed, 1, 0) == 1)
                    return;

                if (Interlocked.CompareExchange(ref _gracefullyClosed, 1,0) == 0)
                    WriteInternal(reason.GetBytes(), 0, 2, true, false, WebSocketFrameOption.ConnectionClose, WebSocketExtensionFlags.None);

                _clientStream.Close();
            }
            catch { }
        }

        private void Dispose(Boolean disposing)
        {
            if (Interlocked.CompareExchange(ref _disposed, 1,0) == 0)
            {
                if (disposing)
                    GC.SuppressFinalize(this);

                try
                {
                    _writeSemaphore.Dispose();
                    this.Close(WebSocketCloseReasons.NormalClose);
                    _clientStream.Dispose();
                }
                catch { }
            }
        }

        public void Dispose()
        {
            Dispose(true);   
        }

        ~WebSocket()
        {
            Dispose(false);
        }
    }

}