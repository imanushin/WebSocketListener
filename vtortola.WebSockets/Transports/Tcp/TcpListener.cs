﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using vtortola.WebSockets.Tools;

#pragma warning disable 420

namespace vtortola.WebSockets.Transports.Tcp
{
    public sealed class TcpListener : Listener
    {
        public const int DEFAULT_BACKLOG_SIZE = 5;

        private const int STATE_LISTENING = 0;
        private const int STATE_ACCEPTING = 1;
        private const int STATE_DISPOSED = 2;

        private static readonly EndPoint[] EmptyEndPoints = new EndPoint[0];
        private static readonly Socket[] EmptySockets = new Socket[0];

        private readonly ILogger log;
        private readonly Socket[] sockets;
        private readonly Task<Socket>[] acceptTasks;
        private readonly SocketAsyncEventArgs[] activeAcceptEvents;
        private readonly SocketAsyncEventArgs[] availableAcceptEvents;
        private readonly EndPoint[] localEndPoints;
        private readonly bool noDelay;
        private readonly int sendTimeout;
        private readonly int receiveTimeout;
        private volatile int lastAcceptSocketIndex;
        private volatile int state;

        /// <inheritdoc />
        public override IReadOnlyCollection<EndPoint> LocalEndpoints => this.localEndPoints;

        public TcpListener(IPEndPoint[] ipEndPoints, WebSocketListenerOptions options)
        {
            if (ipEndPoints == null) throw new ArgumentNullException(nameof(ipEndPoints));
            if (ipEndPoints.Any(p => p == null)) throw new ArgumentException("Null objects passed in array.", nameof(ipEndPoints));
            if (options == null) throw new ArgumentNullException(nameof(options));

            this.log = options.Logger;
            this.noDelay = !(options.UseNagleAlgorithm ?? false);
            this.sendTimeout = (int)Math.Round(options.WebSocketSendTimeout.TotalMilliseconds);
            this.receiveTimeout = (int)Math.Round(options.WebSocketReceiveTimeout.TotalMilliseconds);
            this.sockets = EmptySockets;
            this.localEndPoints = EmptyEndPoints;

            var boundSockets = new Socket[ipEndPoints.Length];
            var boundEndpoints = new EndPoint[ipEndPoints.Length];
            var activeEvents = new SocketAsyncEventArgs[ipEndPoints.Length];
            var availableEvents = new SocketAsyncEventArgs[ipEndPoints.Length];
            try
            {
                for (var i = 0; i < boundSockets.Length; i++)
                {
                    boundSockets[i] = new Socket(ipEndPoints[i].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    boundSockets[i].Bind(ipEndPoints[i]);
                    boundSockets[i].Listen(options.BacklogSize ?? DEFAULT_BACKLOG_SIZE);
                    boundEndpoints[i] = boundSockets[i].LocalEndPoint;
                    activeEvents[i] = CreateSocketAsyncEvent();
                    availableEvents[i] = CreateSocketAsyncEvent();
                }

                this.sockets = boundSockets;
                this.localEndPoints = boundEndpoints;
                this.acceptTasks = new Task<Socket>[boundSockets.Length];
                this.availableAcceptEvents = activeEvents;
                this.activeAcceptEvents = availableEvents;
                boundSockets = null;
            }
            finally
            {
                if (boundSockets != null)
                {
                    foreach (var socket in boundSockets)
                        SafeEnd.Dispose(socket);
                }
            }
        }

        /// <inheritdoc />
        public override async Task<Connection> AcceptConnectionAsync()
        {
            if (Interlocked.CompareExchange(ref this.state, STATE_ACCEPTING, STATE_LISTENING) != STATE_LISTENING)
            {
                this.ThrowIfDisposed();
                throw new InvalidOperationException("Listener is already accepting connection.");
            }

            try
            {
                while (this.state == STATE_ACCEPTING)
                {
                    for (var i = 0; i < this.acceptTasks.Length; i++)
                    {
                        if (this.acceptTasks[i] != null) continue;

                        Swap(ref this.availableAcceptEvents[i], ref this.activeAcceptEvents[i]);

                        this.acceptTasks[i] = AcceptFromSocketAsync(this.sockets[i], this.activeAcceptEvents[i]);
                    }

                    await Task.WhenAny(this.acceptTasks).ConfigureAwait(false);

                    this.lastAcceptSocketIndex++;

                    if (this.lastAcceptSocketIndex > ushort.MaxValue)
                        this.lastAcceptSocketIndex = 0;

                    for (var i = 0; i < this.acceptTasks.Length; i++)
                    {
                        var taskIndex = (this.lastAcceptSocketIndex + i) % this.acceptTasks.Length;
                        var acceptTask = this.acceptTasks[taskIndex];
                        if (acceptTask == null || acceptTask.IsCompleted == false)
                            continue;

                        this.acceptTasks[taskIndex] = null;
                        var error = acceptTask.Exception.Unwrap();
                        if (acceptTask.Status != TaskStatus.RanToCompletion)
                        {
                            if (this.log.IsDebugEnabled && error != null && error is OperationCanceledException == false)
                                this.log.Debug($"Accept on '{this.sockets[taskIndex].LocalEndPoint}' has failed.", error);
                            continue;
                        }

                        return this.AcceptSocketAsConnection(acceptTask);
                    }
                }
                throw new OperationCanceledException();
            }
            finally
            {
                Interlocked.CompareExchange(ref this.state, STATE_LISTENING, STATE_ACCEPTING);
            }
        }
        private Connection AcceptSocketAsConnection(Task<Socket> acceptTask)
        {
            var socket = acceptTask.Result;
            if (this.log.IsDebugEnabled)
                this.log.Debug($"New socket accepted. Remote address: '{socket.RemoteEndPoint}', Local address: {socket.LocalEndPoint}.");

            socket.NoDelay = this.noDelay;
            socket.SendTimeout = this.sendTimeout;
            socket.ReceiveTimeout = this.receiveTimeout;

            return new TcpConnection(socket, false);
        }

        private static Task<Socket> AcceptFromSocketAsync(Socket socket, SocketAsyncEventArgs acceptEvent)
        {
            if (socket == null) throw new ArgumentNullException(nameof(socket));
            if (acceptEvent == null) throw new ArgumentNullException(nameof(acceptEvent));

            var acceptCompletionSource = new TaskCompletionSource<Socket>(TaskCreationOptions.None);
            acceptEvent.UserToken = acceptCompletionSource;
            acceptEvent.AcceptSocket = null;
            try
            {
                if (socket.AcceptAsync(acceptEvent) == false)
                    OnAcceptCompleted(socket, acceptEvent);
            }
            catch (Exception acceptError) when (acceptError.Unwrap() is ThreadAbortException == false)
            {

                acceptCompletionSource.TrySetException(acceptError.Unwrap());
            }

            return acceptCompletionSource.Task;
        }
        private static SocketAsyncEventArgs CreateSocketAsyncEvent()
        {
            var socketAsyncEvent = new SocketAsyncEventArgs();
            socketAsyncEvent.Completed += OnAcceptCompleted;
            return socketAsyncEvent;
        }
        private static void Swap(ref SocketAsyncEventArgs first, ref SocketAsyncEventArgs second)
        {
            var tmp = first;
            first = second;
            second = tmp;
        }
        private static void OnAcceptCompleted(object _, SocketAsyncEventArgs socketAsyncEventArgs)
        {
            if (socketAsyncEventArgs == null) throw new ArgumentNullException(nameof(socketAsyncEventArgs));

            var acceptCompletionSource = (TaskCompletionSource<Socket>)socketAsyncEventArgs.UserToken;
            socketAsyncEventArgs.UserToken = null;

            if (socketAsyncEventArgs.ConnectByNameError != null)
            {
                acceptCompletionSource.TrySetException(socketAsyncEventArgs.ConnectByNameError);
            }
            else if (socketAsyncEventArgs.SocketError != SocketError.Success)
            {
                acceptCompletionSource.TrySetException(new SocketException((int)socketAsyncEventArgs.SocketError));
            }
            else
            {
                var socket = socketAsyncEventArgs.AcceptSocket;
                if (acceptCompletionSource.TrySetResult(socket) == false)
                    SafeEnd.Dispose(socket);
            }
        }

        private void ThrowIfDisposed()
        {
            if (this.state >= STATE_DISPOSED)
                throw new ObjectDisposedException(typeof(TcpListener).FullName);
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposed)
        {
            if (Interlocked.Exchange(ref this.state, STATE_DISPOSED) != STATE_LISTENING)
                return;

            foreach (var socket in this.sockets)
                SafeEnd.Dispose(socket, this.log);
        }
        /// <inheritdoc />
        public override string ToString()
        {
            // ReSharper disable once CoVariantArrayConversion
            return $"{nameof(TcpListener)}, {string.Join(", ", (object[])this.localEndPoints)}";
        }
    }
}