﻿using Newtonsoft.Json;
using RestRPC.Framework.Messages.Inputs;
using RestRPC.Framework.Messages.Outputs;
using RestRPC.Framework.Plugins;
using RestRPC.Framework.Serialization;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using WebSocketSharp;
using WebSocketSharp.Net;

namespace RestRPC.Framework
{
    /// <summary>
    /// RestRPCComponent communicates with an RRPC server and handles in/out messages
    /// </summary>
    public class RrpcComponent
    {
        const int CHANNEL_SIZE = 50;

        WebSocket ws;
        DateTime lastPollTime = DateTime.Now;

        ConcurrentQueue<InMessage> inQueue = new ConcurrentQueue<InMessage>();
        ConcurrentQueue<OutMessage> outQueue = new ConcurrentQueue<OutMessage>();
        ConcurrentQueue<Task> taskQueue = new ConcurrentQueue<Task>();

        InMessageSerializer inMessageSerializer = new InMessageSerializer();
        JsonSerializerSettings outSerializerSettings = new JsonSerializerSettings
        {
            ContractResolver = new WritablePropertiesOnlyResolver(),
            NullValueHandling = NullValueHandling.Ignore
        };

        /// <summary>
        /// Gets the name of this RestRPC component
        /// </summary>
        public string Name
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets information on the remote this component is connecting to
        /// </summary>
        public Uri RemoteUri
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the rate the component polls messages from server
        /// </summary>
        public TimeSpan PollingRate
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets an indicator whether the component is running network updates.
        /// </summary>
        public bool IsRunning
        {
            get;
            private set;
        } = false;

        /// <summary>
        /// Gets the state of WebSocket connection
        /// </summary>
        public ConnectionState ConnectionState
        {
            get;
            private set;
        } = ConnectionState.Disconnected;

        /// <summary>
        /// Gets the instance of PluginManager in this component
        /// </summary>
        public PluginManager PluginManager
        {
            get;
            set;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="componentName">The name of this RestRPC component</param>
        /// <param name="remoteUri">Remote server settings</param>
        /// <param name="pollingRate">Rate to poll messages from server</param>
        /// <param name="username">Username for HTTP auth</param>
        /// <param name="password">Password for HTTP auth</param>
        public RrpcComponent(string componentName, Uri remoteUri, TimeSpan pollingRate, 
            string username, string password)
        : this(componentName, remoteUri, pollingRate, username, password, null, LogType.None)
        { }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="componentName">The name of this RestRPC component</param>
        /// <param name="remoteUri">Remote server settings</param>
        /// <param name="pollingRate">Rate to poll messages from server</param>
        /// <param name="username">Username for HTTP auth</param>
        /// <param name="password">Password for HTTP auth</param>
        /// <param name="logWriter">Log writer</param>
        /// <param name="logLevel">Level of logging</param>
        public RrpcComponent(string componentName, Uri remoteUri, TimeSpan pollingRate, 
            string username, string password, TextWriter logWriter, LogType logLevel)
        {
            this.Name = componentName;
            this.RemoteUri = remoteUri;
            this.PollingRate = pollingRate;

            Logger.Writer = logWriter;
            Logger.LogLevel = logLevel;

            // Set up network worker, which exchanges data between plugin and server
            ws = new WebSocket(remoteUri.ToString());
            ws.OnMessage += WS_OnMessage;
            ws.OnOpen += WS_OnOpen;
            ws.OnClose += WS_OnClose;
            ws.OnError += WS_OnError;
            // HTTP basic auth
            ws.SetCredentials(username, password, true);

            // Create plugin manager instance
            PluginManager = new PluginManager(this);
        }

        /// <summary>
        /// Starts connection to the remote server. 
        /// The component will always attempt to reconnect if disconnected while it is running
        /// </summary>
        public void Start()
        {
            if (!IsRunning)
            {
                // XXX: Set "svcName" cookie so the server knows who we are
                ws.SetCookie(new Cookie("svcName", Name));
                IsRunning = true;
            }
        }

        /// <summary>
        /// Stops connection to the remote server. 
        /// All unprocessed inputs and unsent outputs will be discarded
        /// </summary>
        public void Stop()
        {
            if (IsRunning)
            {
                // Clear queues
                inQueue = new ConcurrentQueue<InMessage>();
                outQueue = new ConcurrentQueue<OutMessage>();

                IsRunning = false;
            }
        }

        /// <summary>
        /// Updates RestRPCComponent. Should be called on every tick.
        /// </summary>
        public void Update()
        {
            // Tick plugin manager
            PluginManager.Update();

            // Process input messages
            ProcessInputMessages();

            // Send messages over websocket
            NetworkUpdate();

            // Start all queued tasks
            RunQueuedTasks();
        }

        /// <summary>
        /// Schedules a task to be run during Update
        /// </summary>
        /// <param name="action"></param>
        /// <returns></returns>
        public Task RunOnUpdateThread(Action action)
        {
            var task = new Task(action);
            taskQueue.Enqueue(task);
            return task;
        }

        internal void EnqueueOutMessage(OutMessage outMessage)
        {
            outQueue.Enqueue(outMessage);
        }

        private void NetworkUpdate()
        {
            // Only perform network update once per polling interval
            if (DateTime.Now - lastPollTime < PollingRate) return;
            lastPollTime = DateTime.Now;

            // Connect ws if running and ws not connected
            if (IsRunning && ConnectionState == ConnectionState.Disconnected)
            {
                ws.ConnectAsync();
                ConnectionState = ConnectionState.Connecting;
            }

            // Disconnect if not running and ws open
            if (!IsRunning && ConnectionState == ConnectionState.Connected)
            {
                ws.CloseAsync(CloseStatusCode.Normal);
                ConnectionState = ConnectionState.Closing;
            }

            // Send messages on socket when connection is open
            if (ConnectionState == ConnectionState.Connected)
            {
                ProcessOutputMessages();
            }
        }

        private void ProcessInputMessages()
        {
            InMessage inMsg;
            while (inQueue.TryDequeue(out inMsg))
            {
                try
                {
                    // Process this message
                    inMsg.Evaluate(this);
                }
                catch (Exception ex)
                {
                    Logger.Log(ex.ToString(), LogType.Error);
                }
            }
        }

        private void RunQueuedTasks()
        {
            Task task;
            while (taskQueue.TryDequeue(out task))
            {
                task.RunSynchronously();
            }
        }

        private void ProcessOutputMessages()
        {
            // Output messages can only be processed when websocket connection is open
            if (ws.ReadyState != WebSocketState.Open)
            {
                throw new Exception("Cannot process output messages when WebSocket connection is not open!");
            }

            // Send output data
            OutMessage outMsg;
            while (outQueue.TryDequeue(out outMsg))
            {
                // Serialize the object to JSON then send back to server.
                try
                {
                    ws.SendAsync(JsonConvert.SerializeObject(outMsg, outSerializerSettings), null);
                }
                catch (Exception sendExc)
                {
                    Logger.Log(sendExc.ToString(), LogType.Error);
                }
            }
        }

        private void WS_OnMessage(object sender, MessageEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;

            try
            {
                InMessage inMsg = JsonConvert.DeserializeObject<InMessage>(e.Data, inMessageSerializer);
                if (inMsg != null)
                {
                    inQueue.Enqueue(inMsg);
                }
            }
            catch (Exception exc)
            {
                Logger.Log("Error parsing InMessage: " + e.Data + ": " + exc.ToString(), LogType.Error);
                // TODO: Compose and enqueue an error out message
            }
        }

        private void WS_OnOpen(object sender, EventArgs e)
        {
            ConnectionState = ConnectionState.Connected;
            Logger.Log("WebSocket connection established: " + ws.Url, LogType.Info);
        }

        private void WS_OnClose(object sender, CloseEventArgs e)
        {
            // This can occur either when socket connect fails or socket disconnects while connected
            if (ConnectionState != ConnectionState.Connecting)
            {
                Logger.Log("WebSocket connection closed: " + ws.Url, LogType.Info);
            }
            ConnectionState = ConnectionState.Disconnected;
        }

        private void WS_OnError(object sender, WebSocketSharp.ErrorEventArgs e)
        {
            Logger.Log("WebSocket error: " + e.Message, LogType.Error);
        }
    }
}
