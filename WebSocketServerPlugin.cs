using System;
using System.Collections.Concurrent;
using BepInEx.Logging;
using Fleck;


namespace FirestoneBepinexModsManager
{
    public class WebSocketServerPlugin
    {
        internal new ManualLogSource Logger;

        private ConcurrentDictionary<Guid, IWebSocketConnection> sockets = new ConcurrentDictionary<Guid, IWebSocketConnection>();

        public WebSocketServerPlugin()
        {
            this.Logger = Plugin.Logger;
        }

        internal string OpenServer(int port, Action onClientConnect, Action<string> onMessage)
        {
            var server = new WebSocketServer($"ws://127.0.0.1:{port}/firestone-mods-manager");
            Logger.LogInfo($"Websocket server element created ");
            server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    sockets.TryAdd(socket.ConnectionInfo.Id, socket);
                    Logger.LogInfo($"Client connected {socket.ConnectionInfo.Id}");
                    onClientConnect();
                };
                socket.OnClose = () =>
                {
                    sockets.TryRemove(socket.ConnectionInfo.Id, out IWebSocketConnection removedSocket);
                    Logger.LogInfo($"Client disconnected {socket.ConnectionInfo.Id}");
                };
                socket.OnMessage = message =>
                {
                    Logger.LogInfo($"received message {message}");
                    onMessage(message);
                };
                Logger.LogInfo($"Websocket server started");
            });
            return server.Location;
        }

        public void Broadcast(string message)
        {
            foreach (var socket in sockets)
            {
                if (socket.Value.IsAvailable)
                {
                    socket.Value.Send(message);
                }
            }
        }

    }
}
