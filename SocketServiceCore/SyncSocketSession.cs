﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SuperSocket.Common;
using SuperSocket.SocketServiceCore.Command;
using System.IO;
using System.Security.Authentication;
using System.Net.Security;

namespace SuperSocket.SocketServiceCore
{
    public class SyncSocketSession<T> : SocketSession<T>
        where T : IAppSession, new()
    {
        /// <summary>
        /// Starts the the session with specified context.
        /// </summary>
        /// <param name="context">The context.</param>
        protected override void Start(SocketContext context)
        {
            Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            InitStream(context);

            SayWelcome();

            string commandLine;

            while (TryGetCommand(out commandLine))
            {
                LastActiveTime = DateTime.Now;
                context.Status = SocketContextStatus.Healthy;

                try
                {
                    ExecuteCommand(commandLine);

                    if (Client == null && !IsClosed)
                    {
                        //Has been closed
                        OnClose();
                        return;
                    }
                }
                catch (SocketException)
                {
                    Close();
                    break;
                }
                catch (Exception e)
                {
                    LogUtil.LogError(AppServer, e);
                    HandleExceptionalError(e);
                }
            }

            if (Client != null)
            {
                Close();
            }
            else if (!IsClosed)
            {
                OnClose();
            }
        }


        public override void Close()
        {
            base.Close();
        }

        private Stream m_Stream;

        private void InitStream(SocketContext context)
        {
            switch (SecureProtocol)
            {
                case (SslProtocols.Tls):
                case (SslProtocols.Ssl3):
                case (SslProtocols.Ssl2):
                    SslStream sslStream = new SslStream(new NetworkStream(Client), false);
                    sslStream.AuthenticateAsServer(AuthenticationManager.GetCertificate(), false, SslProtocols.Default, true);
                    m_Stream = sslStream as Stream;
                    break;
                default:
                    m_Stream = new NetworkStream(Client);
                    break;
            }

            if (context == null)
                m_Reader = new StreamReader(m_Stream, Encoding.Default);
            else
                m_Reader = new StreamReader(m_Stream, context.Charset);
        }

        private StreamReader m_Reader = null;

        protected StreamReader SocketReader
        {
            get { return m_Reader; }
        }

        public override void ApplySecureProtocol(SocketContext context)
        {
            InitStream(context);
        }

        private bool TryGetCommand(out string command)
        {
            command = string.Empty;

            try
            {
                command = m_Reader.ReadLine();
            }
            catch (Exception e)
            {
                LogUtil.LogError(AppServer, e);
                this.Close();
                return false;
            }

            if (string.IsNullOrEmpty(command))
                return false;

            command = command.Trim();

            if (string.IsNullOrEmpty(command))
                return false;

            return true;
        }

        public override void SendResponse(SocketContext context, string message)
        {
            if (string.IsNullOrEmpty(message))
                return;

            if (!message.EndsWith(Environment.NewLine))
                message = message + Environment.NewLine;

            byte[] data = context.Charset.GetBytes(message);

            try
            {
                m_Stream.Write(data, 0, data.Length);
            }
            catch (Exception e)
            {
                LogUtil.LogError(AppServer, e);
                this.Close();
            }
        }

        public override void SendResponse(SocketContext context, byte[] data)
        {
            if (data == null || data.Length <= 0)
                return;

            try
            {
                m_Stream.Write(data, 0, data.Length);
            }
            catch (Exception e)
            {
                LogUtil.LogError(AppServer, e);
                this.Close();
            }
        }

        public override void ReceiveData(Stream storeSteram, int length)
        {
            byte[] buffer = new byte[Client.ReceiveBufferSize];

            int thisRead = 0;
            int leftRead = length;
            int shouldRead = 0;

            while (leftRead > 0)
            {
                shouldRead = Math.Min(buffer.Length, leftRead);
                thisRead = m_Stream.Read(buffer, 0, shouldRead);

                if (thisRead <= 0)
                {
                    //Slow speed? Wait a moment
                    Thread.Sleep(100);
                    continue;
                }

                storeSteram.Write(buffer, 0, thisRead);
                leftRead -= thisRead;
            }
        }

        public override void ReceiveData(Stream storeSteram, byte[] endMark)
        {
            byte[] buffer = new byte[Client.ReceiveBufferSize];
            byte[] lastData = new byte[endMark.Length];
            int lastDataSzie = 0;

            int thisRead = 0;

            while (true)
            {
                thisRead = m_Stream.Read(buffer, 0, buffer.Length);

                if (thisRead > 0)
                {
                    if (thisRead >= endMark.Length)
                    {
                        if (EndsWith(buffer, 0, thisRead, endMark))
                        {
                            storeSteram.Write(buffer, 0, thisRead);
                            return;
                        }
                        else
                        {
                            storeSteram.Write(buffer, 0, thisRead);
                            Array.Copy(buffer, thisRead - endMark.Length - 1, lastData, 0, endMark.Length);
                            lastDataSzie = endMark.Length;
                        }
                    }
                    else
                    {
                        bool matched = false;

                        int searchIndex = endMark.Length - 1;

                        for (int i = thisRead - 1; i >= 0 && searchIndex >= 0; i--, searchIndex--)
                        {
                            if (endMark[searchIndex] != buffer[i])
                            {
                                matched = false;
                                break;
                            }
                            else
                            {
                                matched = true;
                            }
                        }

                        if (lastDataSzie > 0)
                        {
                            for (int i = lastDataSzie - 1; i >= 0 && searchIndex >= 0; i--, searchIndex--)
                            {
                                if (endMark[searchIndex] != lastData[i])
                                {
                                    matched = false;
                                    break;
                                }
                                else
                                {
                                    matched = true;
                                }
                            }
                        }

                        if (matched && searchIndex < 0)
                        {
                            storeSteram.Write(buffer, 0, thisRead);
                            return;
                        }
                        else
                        {
                            storeSteram.Write(buffer, 0, thisRead);

                            if (lastDataSzie + thisRead <= lastData.Length)
                            {
                                Array.Copy(buffer, 0, lastData, lastDataSzie, thisRead);
                                lastDataSzie = lastDataSzie + thisRead;
                            }
                            else
                            {
                                Array.Copy(lastData, thisRead + lastDataSzie - lastData.Length, lastData, 0, lastData.Length - thisRead);
                                Array.Copy(buffer, 0, lastData, lastDataSzie, thisRead);
                                lastDataSzie = endMark.Length;
                            }
                        }
                    }
                }
                else
                {
                    Thread.Sleep(100);
                    continue;
                }
            }
        }
    }
}
