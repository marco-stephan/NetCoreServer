﻿using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace NetCoreServer
{
    /// <summary>
    /// TCP session is used to read and write data from the connected TCP client
    /// </summary>
    /// <remarks>Thread-safe</remarks>
    public class TcpSession
    {
        /// <summary>
        /// Initialize the session with a given server
        /// </summary>
        /// <param name="server">TCP server</param>
        public TcpSession(TcpServer server)
        {
            Id = Guid.NewGuid();
            Server = server;
        }

        /// <summary>
        /// TCP session Id
        /// </summary>
        public Guid Id { get; }

        /// <summary>
        /// Server
        /// </summary>
        public TcpServer Server { get; }
        /// <summary>
        /// Socket
        /// </summary>
        public Socket Socket { get; private set; }

        /// <summary>
        /// Number of bytes pending sent by the session
        /// </summary>
        public long BytesPending { get; private set; }
        /// <summary>
        /// Number of bytes sending by the session
        /// </summary>
        public long BytesSending { get; private set; }
        /// <summary>
        /// Number of bytes sent by the session
        /// </summary>
        public long BytesSent { get; private set; }
        /// <summary>
        /// Number of bytes received by the session
        /// </summary>
        public long BytesReceived { get; private set; }

        /// <summary>
        /// Option: receive buffer size
        /// </summary>
        public int OptionReceiveBufferSize
        {
            get => Socket.ReceiveBufferSize;
            set => Socket.ReceiveBufferSize = value;
        }
        /// <summary>
        /// Option: send buffer size
        /// </summary>
        public int OptionSendBufferSize
        {
            get => Socket.SendBufferSize;
            set => Socket.SendBufferSize = value;
        }

        #region Connect/Disconnect session

        /// <summary>
        /// Is the session connected?
        /// </summary>
        public bool IsConnected { get; private set; }

        /// <summary>
        /// Connect the session
        /// </summary>
        /// <param name="socket">Session socket</param>
        internal void Connect(Socket socket)
        {
            Socket = socket;

            // Apply the option: keep alive
            if (Server.OptionKeepAlive)
                Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            // Apply the option: no delay
            if (Server.OptionNoDelay)
                Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.NoDelay, true);

            // Prepare receive & send buffers
            _receiveBuffer.Reserve(OptionReceiveBufferSize);
            _sendBufferMain.Reserve(OptionSendBufferSize);
            _sendBufferFlush.Reserve(OptionSendBufferSize);

            // Reset statistic
            BytesPending = 0;
            BytesSending = 0;
            BytesSent = 0;
            BytesReceived = 0;

            // Update the connected flag
            IsConnected = true;

            // Call the session connected handler
            OnConnected();

            // Call the session connected handler in the server
            Server.OnConnectedInternal(this);

            // Call the empty send buffer handler
            if (_sendBufferMain.IsEmpty)
                OnEmpty();

            // Try to receive something from the client
            TryReceive();

            // Setup event args
            _receiveEventArg.Completed += Socket_Completed;
            _sendEventArg.Completed += Socket_Completed;
        }

        /// <summary>
        /// Disconnect the session
        /// </summary>
        /// <returns>'true' if the section was successfully disconnected, 'false' if the section is already disconnected</returns>
        public virtual bool Disconnect()
        {
            if (!IsConnected)
                return false;

            // Reset event args
            _receiveEventArg.Completed -= Socket_Completed;
            _sendEventArg.Completed -= Socket_Completed;

            try
            {
                // Close the socket associated with the client
                Socket.Shutdown(SocketShutdown.Send);

                // Close the session socket
                Socket.Close();
            }
            catch (ObjectDisposedException) {}

            // Update the connected flag
            IsConnected = false;

            // Clear send/receive buffers
            ClearBuffers();

            // Call the session disconnected handler
            OnDisconnected();

            // Call the session disconnected handler in the server
            Server.OnDisconnectedInternal(this);

            // Unregister session
            Server.UnregisterSession(Id);

            return true;
        }

        #endregion

        #region Send/Recieve data

        // Receive buffer & cache
        private bool _receiving;
        private readonly Buffer _receiveBuffer = new Buffer();
        private readonly SocketAsyncEventArgs _receiveEventArg = new SocketAsyncEventArgs();
        // Send buffer & cache
        private readonly object _sendLock = new object();
        private bool _sending;
        private Buffer _sendBufferMain = new Buffer();
        private Buffer _sendBufferFlush = new Buffer();
        private readonly SocketAsyncEventArgs _sendEventArg = new SocketAsyncEventArgs();
        private long _sendBufferFlushOffset;

        /// <summary>
        /// Send data into the session
        /// </summary>
        /// <param name="buffer">Buffer to send</param>
        /// <returns>'true' if the data was successfully sent, 'false' if the session is not connected</returns>
        public virtual bool Send(byte[] buffer) { return Send(buffer, 0, buffer.Length); }

        /// <summary>
        /// Send data into the session
        /// </summary>
        /// <param name="buffer">Buffer to send</param>
        /// <param name="offset">Buffer offset</param>
        /// <param name="size">Buffer size</param>
        /// <returns>'true' if the data was successfully sent, 'false' if the session is not connected</returns>
        public virtual bool Send(byte[] buffer, long offset, long size)
        {
            if (!IsConnected)
                return false;

            if (size == 0)
                return true;

            lock (_sendLock)
            {
                // Detect multiple send handlers
                bool sendRequired = _sendBufferMain.IsEmpty || _sendBufferFlush.IsEmpty;                

                // Fill the main send buffer
                _sendBufferMain.Append(buffer, offset, size);

                // Update statistic
                BytesPending = _sendBufferMain.Size;

                // Avoid multiple send handlers
                if (!sendRequired)
                    return true;
            }

            // Try to send the main buffer
            TrySend();

            return true;
        }

        /// <summary>
        /// Send text into the session
        /// </summary>
        /// <param name="text">Text string to send</param>
        /// <returns>'true' if the text was successfully sent, 'false' if the session is not connected</returns>
        public virtual bool Send(string text) { return Send(Encoding.UTF8.GetBytes(text)); }

        /// <summary>
        /// Try to receive new data
        /// </summary>
        private void TryReceive()
        {
            if (_receiving)
                return;

            if (!IsConnected)
                return;

            try
            {
                // Async receive with the receive handler
                _receiving = true;
                _receiveEventArg.SetBuffer(_receiveBuffer.Data, 0, (int)_receiveBuffer.Capacity);
                if (!Socket.ReceiveAsync(_receiveEventArg))
                    ProcessReceive(_receiveEventArg);
            }
            catch (ObjectDisposedException) {}
        }

        /// <summary>
        /// Try to send pending data
        /// </summary>
        private void TrySend()
        {
            if (_sending)
                return;

            if (!IsConnected)
                return;

            // Swap send buffers
            if (_sendBufferFlush.IsEmpty)
            {
                lock (_sendLock)
                {
                    // Swap flush and main buffers
                    _sendBufferFlush = Interlocked.Exchange(ref _sendBufferMain, _sendBufferFlush);
                    _sendBufferFlushOffset = 0;

                    // Update statistic
                    BytesPending = 0;
                    BytesSending += _sendBufferFlush.Size;                    
                }
            }
            else
                return;

            // Check if the flush buffer is empty
            if (_sendBufferFlush.IsEmpty)
            {
                // Call the empty send buffer handler
                OnEmpty();
                return;
            }

            try
            {
                // Async write with the write handler
                _sending = true;
                _sendEventArg.SetBuffer(_sendBufferFlush.Data, (int)_sendBufferFlushOffset, (int)(_sendBufferFlush.Size - _sendBufferFlushOffset));
                if (!Socket.SendAsync(_sendEventArg))
                    ProcessSend(_sendEventArg);
            }
            catch (ObjectDisposedException) {}
        }

        /// <summary>
        /// Clear send/receive buffers
        /// </summary>
        private void ClearBuffers()
        {
            lock (_sendLock)
            {
                // Clear send buffers
                _sendBufferMain.Clear();
                _sendBufferFlush.Clear();
                _sendBufferFlushOffset= 0;

                // Update statistic
                BytesPending = 0;
                BytesSending = 0;
            }
        }

        #endregion

        #region IO processing

        /// <summary>
        /// This method is called whenever a receive or send operation is completed on a socket
        /// </summary>
        private void Socket_Completed(object sender, SocketAsyncEventArgs e)
        {
            // Determine which type of operation just completed and call the associated handler
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Receive:
                    ProcessReceive(e);
                    break;
                case SocketAsyncOperation.Send:
                    ProcessSend(e);
                    break;
                default:
                    throw new ArgumentException("The last operation completed on the socket was not a receive or send");
            }

        }

        /// <summary>
        /// This method is invoked when an asynchronous receive operation completes.
        /// If the remote host closed the connection, then the socket is closed.
        /// If data was received then the data is echoed back to the client.
        /// </summary>
        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            _receiving = false;

            if (!IsConnected)
                return;

            long size = e.BytesTransferred;

            // Received some data from the client
            if (size > 0)
            {
                // Update statistic
                BytesReceived += size;
                Server.BytesReceived += size;

                // If the receive buffer is full increase its size
                if (_receiveBuffer.Capacity == size)
                    _receiveBuffer.Reserve(2 * size);

                // Call the buffer received handler
                OnReceived(_receiveBuffer.Data, size);
            }

            // Try to receive again if the session is valid
            if (e.SocketError == SocketError.Success)
                TryReceive();
            else
            {
                SendError(e.SocketError);
                Disconnect();
            }
        }

        /// <summary>
        /// This method is invoked when an asynchronous send operation completes.  
        /// The method issues another receive on the socket to read any additional
        /// data sent from the client.
        /// </summary>
        private void ProcessSend(SocketAsyncEventArgs e)
        {
            _sending = false;

            if (!IsConnected)
                return;

            long size = e.BytesTransferred;

            // Send some data to the client
            if (size > 0)
            {
                // Update statistic
                BytesSending -= size;
                BytesSent += size;
                Server.BytesSent += size;

                // Increase the flush buffer offset
                _sendBufferFlushOffset += size;

                // Successfully send the whole flush buffer
                if (_sendBufferFlushOffset == _sendBufferFlush.Size)
                {
                    // Clear the flush buffer
                    _sendBufferFlush.Clear();
                    _sendBufferFlushOffset = 0;
                }

                // Call the buffer sent handler
                OnSent(size, BytesPending);
            }

            // Try to send again if the session is valid
            if (e.SocketError == SocketError.Success)
                TrySend();
            else
            {
                SendError(e.SocketError);
                Disconnect();
            }
        }

        #endregion

        #region Session handlers

        /// <summary>
        /// Handle client connected notification
        /// </summary>
        protected virtual void OnConnected() {}
        /// <summary>
        /// Handle client disconnected notification
        /// </summary>
        protected virtual void OnDisconnected() {}

        /// <summary>
        /// Handle buffer received notification
        /// </summary>
        /// <param name="buffer">Received buffer</param>
        /// <param name="size">Received buffer size</param>
        /// <remarks>
        /// Notification is called when another chunk of buffer was received from the client
        /// </remarks>
        protected virtual void OnReceived(byte[] buffer, long size) {}
        /// <summary>
        /// Handle buffer sent notification
        /// </summary>
        /// <param name="sent">Size of sent buffer</param>
        /// <param name="pending">Size of pending buffer</param>
        /// <remarks>
        /// Notification is called when another chunk of buffer was sent to the client.
        /// This handler could be used to send another buffer to the client for instance when the pending size is zero.
        /// </remarks>
        protected virtual void OnSent(long sent, long pending) {}

        /// <summary>
        /// Handle empty send buffer notification
        /// </summary>
        /// <remarks>
        /// Notification is called when the send buffer is empty and ready for a new data to send.
        /// This handler could be used to send another buffer to the client.
        /// </remarks>
        protected virtual void OnEmpty() {}

        /// <summary>
        /// Handle error notification
        /// </summary>
        /// <param name="error">Socket error code</param>
        protected virtual void OnError(SocketError error) {}

        #endregion

        #region Error handling

        /// <summary>
        /// Send error notification
        /// </summary>
        /// <param name="error">Socket error code</param>
        private void SendError(SocketError error)
        {
            // Skip disconnect errors
            if ((error == SocketError.ConnectionAborted) ||
                (error == SocketError.ConnectionRefused) ||
                (error == SocketError.ConnectionReset) ||
                (error == SocketError.OperationAborted))
                return;

            OnError(error);
        }

        #endregion
    }
}