using BeaverBuddies.IO;
using BeaverBuddies.Steam;
using BeaverBuddies.Util;
using Steamworks;
using System;
using System.IO;
using System.Net.Sockets;
using System.Net;
using Timberborn.CoreUI;
using Timberborn.GameSaveRepositorySystem;
using Timberborn.GameSceneLoading;
using Timberborn.Localization;
using Timberborn.SingletonSystem;
using Timberborn.WebNavigation;
using TimberNet;
using System.Linq;
using Timberborn.SettlementNameSystem;

namespace BeaverBuddies.Connect
{
    public class ClientConnectionService : IUpdatableSingleton
    {
        private GameSceneLoader _gameSceneLoader;
        private GameSaveRepository _gameSaveRepository;
        private DialogBoxShower _dialogBoxShower;
        private UrlOpener _urlOpener;
        private ClientEventIO client;
        private Settings _settings;

        // How this player last joined a host, for the desync dialog's reconnect (see Reconnect). Static because it
        // must outlive the scene that joined: the join loads the host's game, which has a new instance of this service.
        private static JoinRoute lastJoin;

        public ClientConnectionService(
            GameSceneLoader gameSceneLoader,
            GameSaveRepository gameSaveRepository,
            DialogBoxShower dialogBoxShower,
            UrlOpener urlOpener,
            Settings settings
        )
        {
            _gameSceneLoader = gameSceneLoader;
            _gameSaveRepository = gameSaveRepository;
            _dialogBoxShower = dialogBoxShower;
            _urlOpener = urlOpener;
            _settings = settings;
        }

        public bool TryToConnect(CSteamID friendID)
        {
            // The connection is established in the background by Steam networking; see SteamLinkSocket.
            var socket = SteamNet.Manager?.Connect(friendID.m_SteamID);
            if (socket == null)
            {
                Plugin.LogError("Steam networking is not ready, so the host could not be reached.");
                return false;
            }
            if (!TryToConnect(socket)) return false;
            lastJoin = JoinRoute.ViaSteam(friendID.m_SteamID);
            return true;
        }

        public bool TryToConnect(string address)
        {
            string typed = address;
            int port = _settings.DefaultPort.Value;
            Plugin.Log("Try to resolve address: " + address);
            // Parse address and port
            if (TryParseHostAndPort(address, out string parsedAddress, out int? parsedPort))
            {
                address = parsedAddress;
                Plugin.Log($"Parsed address: {address}, port: {port}");
            }
            else
            {
                ShowError("BeaverBuddies.JoinCoopGame.Error.InvalidFormat");
                return false;
            }

            // Set port if provided
            if (parsedPort.HasValue)
            {
                port = parsedPort.Value;
            }


            // If it's not an IP address, resolve the hostname
            if (!IPAddress.TryParse(address, out _))
            {

                // Resolve the address if it's a hostname
                if (ResolveHostnameIfNecessary(parsedAddress, out string resolvedAddress))
                {
                    address = resolvedAddress;
                }
                else
                {
                    ShowError("BeaverBuddies.JoinCoopGame.Error.InvalidAddress");
                    return false;
                }
            }

            if (!TryToConnect(new TCPClientWrapper(address, port))) return false;
            lastJoin = JoinRoute.ViaAddress(typed);
            return true;
        }

        private bool TryToConnect(ISocketStream socket)
        {
            Plugin.Log("Connecting client");
            client = ClientEventIO.Create(socket, LoadMap, (error) =>
            {
                // Only reached while joining, before the host's game has loaded (see ClientEventIO): once a game
                // exists, a lost connection is reported by that game, because this service's dialogs belong to the
                // scene that started the join. In 1.0.4 showing one after the game loaded threw, and the uncaught
                // exception crashed the game.
                ShowSafely(() => ShowError("BeaverBuddies.JoinCoopGame.Error.CouldNotConnect", error));
            });
            
            if (client == null)
            {
                Plugin.Log("Client creation failed.");
                return false;
            }

            EventIO.Set(client);
            return true;
        }

        public void ConnectOrShowFailureMessage()
        {
            ConnectOrShowFailureMessage(_settings.ClientConnectionAddress.Value);
        }

        public void ConnectOrShowFailureMessage(string address)
        {
            TryToConnect(address);
        }

        /// <summary>
        /// A guest's "Reconnect (wait for Rehost)" after a desync: joins the host again the way it joined before. It
        /// used to dial the address in the settings whatever the route, so a guest who had joined over Steam dialled
        /// 127.0.0.1 (the default) or a stale address. See DesyncDialogPlan.Reconnect.
        /// </summary>
        public void Reconnect()
        {
            ReconnectPlan plan = DesyncDialogPlan.Reconnect(lastJoin, _settings.ClientConnectionAddress.Value, FindHostLobby);
            Plugin.Log($"Reconnecting after a desync; joined by {lastJoin?.ToString() ?? "an unknown route"}: {plan.Step}");
            switch (plan.Step)
            {
                case ReconnectStep.JoinSteamLobby:
                    try
                    {
                        // Entering the lobby connects to the host, as accepting an invite does
                        // (SteamOverlayConnectionService.OnLobbyEntered, which also refuses a lobby whose game has started).
                        SteamMatchmaking.JoinLobby(new CSteamID(plan.Lobby));
                    }
                    catch (Exception error)
                    {
                        Plugin.LogWarning("Could not join the host's Steam lobby: " + error.Message);
                        ShowWaitForSteamInvite();
                    }
                    break;
                case ReconnectStep.WaitForSteamInvite:
                    ShowWaitForSteamInvite();
                    break;
                default:
                    ConnectOrShowFailureMessage(plan.Address);
                    break;
            }
        }

        private void ShowWaitForSteamInvite()
        {
            ShowSafely(() => _dialogBoxShower.Create()
                .SetLocalizedMessage("BeaverBuddies.ClientDesynced.WaitForSteamInvite")
                .Show());
        }

        // The Steam lobby the host is in, when Steam shows it to this player: the host's lobby is friends-only when it
        // has "Allow Friends to Join Directly via Steam" on, and then its friends see it; an invite-only lobby is not.
        private static ulong? FindHostLobby(ulong host)
        {
            try
            {
                if (SteamFriends.GetFriendGamePlayed(new CSteamID(host), out FriendGameInfo_t game) && game.m_steamIDLobby.IsValid())
                {
                    return game.m_steamIDLobby.m_SteamID;
                }
            }
            catch (Exception error)
            {
                Plugin.LogWarning("Could not look up the host's Steam lobby: " + error.Message);
            }
            return null;
        }

        /// <summary>Shows the standard "could not join" dialog with a specific reason.</summary>
        public void ShowJoinError(string reasonKey, string details = null)
        {
            ShowSafely(() => ShowError(reasonKey, details));
        }

        // These are reached from Steam callbacks, which do not care which scene is loaded. A message that
        // cannot be shown is logged; it must never take the game down with it.
        private static void ShowSafely(Action show)
        {
            try { show(); }
            catch (Exception error) { Plugin.LogWarning("Could not show a multiplayer message: " + error.Message); }
        }

        public void ShowConnectionMessage(bool success)
        {
            ShowSafely(() =>
            {
                if (success)
                {
                    _dialogBoxShower.Create()
                        .SetLocalizedMessage("BeaverBuddies.JoinCoopGame.Success")
                        .Show();
                }
                else
                {
                    ShowError("BeaverBuddies.JoinCoopGame.ConnectionFailedMessage");
                }
            });
        }

        private void ShowError(string reasonKey, string details = null)
        {
            string messageKey;
            if (reasonKey != null)
            {
                messageKey = "BeaverBuddies.JoinCoopGame.ConnectionFailedMessageWithError";
            }
            else
            {
                messageKey = "BeaverBuddies.JoinCoopGame.ConnectionFailedMessage";
            }

            ILoc _loc = _dialogBoxShower._loc;
            string reasonMessage = null;
            if (reasonKey != null)
            {
                reasonMessage = _loc.T(reasonKey);
            }

            if (details != null)
            {
                if (reasonMessage != null)
                {
                    reasonMessage += "\n";
                }
                else
                {
                    reasonMessage = "";
                }
                reasonMessage += "\"" + details + "\"";
            }

            var action = () =>
            {
                _urlOpener.OpenUrl(LinkHelper.TroubleshootingUrl);
            };

            string message = _loc.T(messageKey, reasonMessage);
            _dialogBoxShower.Create()
                .SetMessage(message)
                .SetConfirmButton(action)
                .SetDefaultCancelButton()
                .Show();
        }

        private void LoadMap(byte[] mapBytes)
        {
            // Clean up our current co-op state before loading,
            // so we don't, for example, end up ticking the client before
            // it's actually loaded.
            SingletonManager.Reset();

            Plugin.Log("Loading map");
            //string saveName = Guid.NewGuid().ToString();
            string saveName = TimberNetBase.GetHashCode(mapBytes).ToString("X8");
            SaveReference saveRef = new SaveReference("Online Games", new SettlementReference(saveName, _gameSaveRepository.DefaultSaveDirectory));
            Stream stream = _gameSaveRepository.CreateSaveSkippingNameValidation(saveRef);
            stream.Write(mapBytes);
            stream.Close();

            // Set the RNG seed before loading the map
            // The server does the same
            DeterminismService.InitGameStartState(mapBytes);
            _gameSceneLoader.StartSaveGame(saveRef);
        }

        public void UpdateSingleton()
        {
            if (client == null) return;
            //Plugin.Log("Updating client!");
            client.Update();
        }

        /// <summary>
        /// Tries to parse a host:port or [IPv6]:port string.
        /// Supports IPv4, IPv6, and hostnames.
        /// Returns true if parsing succeeded.
        /// </summary>
        public static bool TryParseHostAndPort(
            string input,
            out string host,
            out int? port)
        {
            host = null;
            port = null;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            // Uri requires a scheme, so we prepend a dummy one
            var uriString = input.Contains("://") ? input : "tcp://" + input;

            if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
            {
                if (!input.StartsWith("[") && input.Count(c => c == ':') >= 2)
                {
                    Plugin.Log("Attempting to wrap likely IPv6 address");
                    return TryParseHostAndPort($"[{input}]", out host, out port);
                }
                return false;
            }

            // Hostname or IP string (IPv6 brackets stripped)
            host = uri.Host;

            // Port: Uri.Port returns -1 if missing
            if (uri.Port != -1)
                port = uri.Port;

            return true;
        }

        private bool ResolveHostnameIfNecessary(string address, out string resolvedAddress)
        {
            resolvedAddress = null;

            try
            {
                // Otherwise, try to resolve it
                IPHostEntry hostEntry = Dns.GetHostEntry(address);
                if (hostEntry.AddressList.Length > 0)
                {
                    resolvedAddress = hostEntry.AddressList[0].ToString();
                    Plugin.Log(address + " resolved to " + resolvedAddress);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogError("Could not resolve hostname: " + ex.ToString());
            }

            return false;
        }
    }
}
