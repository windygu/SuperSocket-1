﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel.Description;
using System.ServiceModel.Security;
using System.Text;
using SuperSocket.Common;
using SuperSocket.SocketBase;
using SuperSocket.SocketBase.Config;
using SuperSocket.SocketBase.Protocol;

namespace SuperSocket.SocketEngine
{
    public static partial class SocketServerManager
    {
        /// <summary>
        /// main AppServer instances list
        /// </summary>
        private static List<IAppServer> m_ServerList = new List<IAppServer>();

        /// <summary>
        /// generic helper server instances list
        /// </summary>
        private static List<IGenericServer> m_GenericServerList = new List<IGenericServer>();

        private static Dictionary<string, Type> m_ServiceDict = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        
        private static Dictionary<string, IConnectionFilter> m_ConnectionFilterDict = new Dictionary<string, IConnectionFilter>(StringComparer.OrdinalIgnoreCase);

        private static IConfig m_Config;

        /// <summary>
        /// Indicate whether the server has been initialized
        /// </summary>
        private static bool m_Initialized = false;

        /// <summary>
        /// Initializes SuperSocket with the specified config.
        /// </summary>
        /// <param name="config">The config.</param>
        /// <param name="serverConfigResolver">The server config resolver.</param>
        /// <returns></returns>
        public static bool Initialize(IConfig config, Func<IServerConfig, IServerConfig> serverConfigResolver)
        {
            if (m_Initialized)
                throw new Exception("The server had been initialized already, you cannot initialize it again!");

            m_Config = config;

            //Initialize services
            foreach (var service in config.Services)
            {
                if (service.Disabled)
                    continue;
                
                Type serviceType;

                if (!AssemblyUtil.TryGetType(service.Type, out serviceType))
                {
                    LogUtil.LogError("Failed to initialize service " + service.Name + "!");
                    return false;
                }

                m_ServiceDict[service.Name] = serviceType;
            }
            
            //Initialize connection filter
            foreach (var filter in config.ConnectionFilters)
            {
                
                Type filterType;

                if (!AssemblyUtil.TryGetType(filter.Type, out filterType))
                {
                    LogUtil.LogError("Failed to initialize connection filter " + filter.Name + "!");
                    return false;
                }
                
                IConnectionFilter filterInstance;
                
                try
                {
                    filterInstance = (IConnectionFilter)Activator.CreateInstance(filterType);
                }
                catch (Exception e)
                {
                    LogUtil.LogError(string.Format("Failed to initialize filter instance {0}!", filter.Name), e);
                    return false;
                }
                
                if(!filterInstance.Initialize(filter.Name, filter.Options))
                {
                    LogUtil.LogError(string.Format("Failed to initialize filter instance {0}!", filter.Name));
                    return false;
                }
                
                m_ConnectionFilterDict[filter.Name] = filterInstance;
            }

            //Initialize generic servers
            foreach (var genericServerConfig in config.GenericServers)
            {
                Type serverType;

                if (!AssemblyUtil.TryGetType(genericServerConfig.Type, out serverType))
                {
                    LogUtil.LogError("Failed to initialize generic server type " + genericServerConfig.Name + "!");
                    continue;
                }

                IGenericServer serverInstance;

                try
                {
                    serverInstance = (IGenericServer)Activator.CreateInstance(serverType);
                    if(!serverInstance.Initialize(genericServerConfig, LogUtil.GetRootLogger()))
                        throw new Exception("Failed to initialize GeneriServer!");

                    m_GenericServerList.Add(serverInstance);                    
                }
                catch (Exception e)
                {
                    LogUtil.LogError("Failed to initialize generic server instance " + genericServerConfig.Name + "!", e);
                    continue;
                }
            }

            //Initialize servers
            foreach (var serverConfig in config.Servers)
            {
                if (!InitializeServer(serverConfigResolver(serverConfig)))
                {
                    LogUtil.LogError("Failed to initialize server " + serverConfig.Name + "!");
                    return false;
                }
            }

            m_Initialized = true;

            return true;
        }

        /// <summary>
        /// Initializes SuperSocket with the specified config.
        /// </summary>
        /// <param name="config">The config.</param>
        /// <returns></returns>
        public static bool Initialize(IConfig config)
        {
            return Initialize(config, c => c);
        }

        /// <summary>
        /// Initializes the specified servers.
        /// </summary>
        /// <param name="servers">The passed in AppServers, which have been setup.</param>
        /// <returns></returns>
        public static bool Initialize(IEnumerable<IAppServer> servers)
        {
            m_ServerList.AddRange(servers);
            m_Initialized = true;
            return true;
        }

        private static bool InitializeServer(IServerConfig serverConfig)
        {
            if (serverConfig.Disabled)
                return true;

            Type serviceType = null;

            if (!m_ServiceDict.TryGetValue(serverConfig.ServiceName, out serviceType))
            {
                LogUtil.LogError(string.Format("The service {0} cannot be found in configuration!", serverConfig.ServiceName));
                return false;
            }

            IAppServer server;

            try
            {
                server = (IAppServer)Activator.CreateInstance(serviceType);
            }
            catch (Exception e)
            {
                LogUtil.LogError("Failed to create server instance!", e);
                return false;
            }

            if (!server.Setup(m_Config, serverConfig, SocketServerFactory.Instance))
            {
                LogUtil.LogError("Failed to setup server instance!");
                return false;
            }
            
            if(!string.IsNullOrEmpty(serverConfig.ConnectionFilters))
            {
                var filters = serverConfig.ConnectionFilters.Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                
                if(filters != null && filters.Length > 0)
                {
                    var filterInstances = new List<IConnectionFilter>(filters.Length);
                    
                    foreach(var f in filters)
                    {
                        IConnectionFilter currentFilter;
                        if(!m_ConnectionFilterDict.TryGetValue(f, out currentFilter))
                        {
                            LogUtil.LogError(string.Format("Failed to find a connection filter '{0}'!", f)); 
                            return false;
                        }
                        filterInstances.Add(currentFilter);
                    }
                    
                    server.ConnectionFilters = filterInstances;
                }
            }

            m_ServerList.Add(server);
            return true;
        }

        public static bool Start()
        {
            foreach (IAppServer server in m_ServerList)
            {
                if (!server.Start())
                {
                    LogUtil.LogError("Failed to start " + server.Name + " server!");
                }
                else
                {
                    LogUtil.LogInfo(server.Name + " has been started");
                }
            }

            StartPerformanceLog();

            try
            {
                foreach (var server in m_GenericServerList)
                {
                    server.Start();
                }
            }
            catch (Exception e)
            {
                LogUtil.LogError(e);
            }

            return true;
        }

        /// <summary>
        /// Stops this instance.
        /// </summary>
        public static void Stop()
        {
            foreach (var server in m_ServerList)
            {
                server.Stop();
                LogUtil.LogInfo(server.Name + " has been stopped");
            }

            StopPerformanceLog();

            try
            {
                foreach (var server in m_GenericServerList)
                {
                    server.Stop();
                }
            }
            catch (Exception e)
            {
                LogUtil.LogError(e);
            }
        }

        public static IServiceConfig GetServiceConfig(string name)
        {
            foreach (var config in m_Config.Services)
            {
                if (string.Compare(config.Name, name, true) == 0)
                {
                    return config;
                }
            }
            return null;
        }


        public static IAppServer GetServerByName(string name)
        {
            return m_ServerList.SingleOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
