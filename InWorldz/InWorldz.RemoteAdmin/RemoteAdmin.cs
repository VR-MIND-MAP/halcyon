/*
 * Copyright (c) 2015, InWorldz Halcyon Developers
 * All rights reserved.
 * 
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 * 
 *   * Redistributions of source code must retain the above copyright notice, this
 *     list of conditions and the following disclaimer.
 * 
 *   * Redistributions in binary form must reproduce the above copyright notice,
 *     this list of conditions and the following disclaimer in the documentation
 *     and/or other materials provided with the distribution.
 * 
 *   * Neither the name of halcyon nor the names of its
 *     contributors may be used to endorse or promote products derived from
 *     this software without specific prior written permission.
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
 * FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
 * DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
 * OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Xml;
using System.Net;
using System.Reflection;
using System.Timers;

using log4net;
using Nini.Config;
using Nwc.XmlRpc;

using OpenMetaverse;

using OpenSim;
using OpenSim.Framework;
using OpenSim.Framework.Communications;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.CoreModules.World.Terrain;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace InWorldz.RemoteAdmin
{
    public class RemoteAdmin
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private Timer sessionTimer;
        private Dictionary<UUID, DateTime> m_activeSessions = 
            new Dictionary<UUID, DateTime>();
        private Dictionary<string, Dictionary<string, XmlMethodHandler>> m_commands = 
            new Dictionary<string, Dictionary<string, XmlMethodHandler>>();

        public delegate object XmlMethodHandler(IList args, IPEndPoint client);

        public RemoteAdmin()
        {
            AddCommand("session", "login_with_password", SessionLoginWithPassword);
            AddCommand("session", "logout", SessionLogout);

            AddCommand("Console", "Command", ConsoleCommandHandler);

            // AddCommand("GridService", "Shutdown", RegionShutdownHandler);

            sessionTimer = new Timer(60000); // 60 seconds
            sessionTimer.Elapsed += sessionTimer_Elapsed;
            sessionTimer.Enabled = true;
        }

        /// <summary>
        /// Called publicly by server code that is not hosting a scene, but wants remote admin support
        /// </summary>
        /// <param name="server"></param>
        public void AddHandler(BaseHttpServer server)
        {
            m_log.Info("[RADMIN]: Remote Admin CoreInit");

            server.AddStreamHandler(new XmlRpcStreamHandler("POST", Util.XmlRpcRequestPrefix("RemoteAdmin"), XmlRpcCommand));
        }

        public void AddCommand(string classname, string command, XmlMethodHandler method)
        {
            lock (m_commands)
            {
                if (!m_commands.ContainsKey(classname))
                    m_commands.Add(classname, new Dictionary<string, XmlMethodHandler>());
                m_commands[classname].Add(command, method);
            }
        }

        public void RemoveCommand(string classname, string command, XmlMethodHandler method)
        {
            lock (m_commands)
            {
                if (!m_commands.ContainsKey(classname))
                    return;
                m_commands[classname].Remove(command);
            }
        }

        private XmlMethodHandler LookupCommand(string classname, string command)
        {
            lock (m_commands)
            {
                if (!m_commands.ContainsKey(classname))
                    return (null);
                if (m_commands[classname].ContainsKey(command))
                    return m_commands[classname][command];
                else
                    return (null);
            }
        }

        public XmlRpcResponse XmlRpcCommand(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();

            try
            {
                responseData["Status"] = "Success";
                responseData["Value"] = String.Empty;

                XmlMethodHandler handler = LookupCommand(request.MethodNameObject, request.MethodNameMethod);

                if (handler != null)
                {
                    responseData["Value"] = handler(request.Params, remoteClient);
                    response.Value = responseData;
                }
                else
                {
                    // Code set in accordance with http://xmlrpc-epi.sourceforge.net/specs/rfc.fault_codes.php
                    response.SetFault(
                        XmlRpcErrorCodes.SERVER_ERROR_METHOD,
                        String.Format("Requested method [{0}] not found", request.MethodNameObject + "." + request.MethodNameMethod));
                }
            }
            catch (Exception e)
            {
                responseData["Status"] = "Failure";
                responseData["ErrorDescription"] = e.Message;
                response.Value = responseData;
            }

            return response;
        }

        public void CheckSessionValid(UUID sessionid)
        {
            lock (m_activeSessions)
            {
                if (!m_activeSessions.ContainsKey(sessionid))
                    throw new Exception("SESSION_INVALID");
                m_activeSessions[sessionid] = DateTime.Now;
            }
        }

        // If a session has been inactive for 10 minutes, time it out.
        private void sessionTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            List<UUID> expiredSessions = new List<UUID>();

            lock (m_activeSessions)
            {
                foreach (UUID key in m_activeSessions.Keys)
                {
                    if ((DateTime.Now - m_activeSessions[key]) > TimeSpan.FromMinutes(10))
                        expiredSessions.Add(key);
                }

                foreach (UUID key in expiredSessions)
                {
                    m_activeSessions.Remove(key);
                }
            }
        }

        private object SessionLoginWithPassword(IList args, IPEndPoint remoteClient)
        {
            UUID sessionId;
            string username = (string)args[0];
            string password = (string)args[1];

            // Is the username the same as the logged in user and do they have the password correct?
            if ( Util.AuthenticateAsSystemUser(username, password))
            {
                lock (m_activeSessions)
                {
                    sessionId = UUID.Random();
                    m_activeSessions.Add(sessionId, DateTime.Now);
                }
            }
            else
            {
                throw new Exception("Invalid Username or Password");
            }

            return (sessionId.ToString());
        }

        private object SessionLogout(IList args, IPEndPoint remoteClient)
        {
            UUID sessionId = new UUID((string)args[0]);

            lock (m_activeSessions)
            {
                if (m_activeSessions.ContainsKey(sessionId))
                {
                    m_activeSessions.Remove(sessionId);
                    return (true);
                }
                else
                {
                    return (false);
                }
            }
        }

        private object ConsoleCommandHandler(IList args, IPEndPoint client)
        {
            CheckSessionValid(new UUID((string)args[0]));

            string command = (string)args[1];
            MainConsole.Instance.RunCommand(command);
            return String.Empty;
        }

        public void Dispose()
        {
        }

    }

}
