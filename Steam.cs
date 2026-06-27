using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Authentication;

namespace DiscordBotTTS
{

    // Most of this is from the sample https://github.com/SteamRE/SteamKit/blob/master/Samples/4.Friends/Program.cs
    public class Message
    {
        public string Msg { get; set; }
        public DateTime Time { get; set; }
        public ulong UserId { get; set; }
        public string UserName { get; set; }
    }
    public static class Steam
    {
        static SteamClient steamClient;
        static CallbackManager manager;

        static SteamUser steamUser;
        static SteamFriends steamFriends;

        static volatile bool isRunning;

        static int errorCount = 0;

        // Set when login fails in a way that retrying cannot fix (e.g. SteamGuard
        // protection or bad credentials). Stops the reconnect loop entirely.
        static volatile bool permanentFailure = false;

        // Token-based auth state (the modern Steam login flow). Persisted to
        // steamauth.json so restarts skip the credential + Steam Guard handshake.
        const string AuthFilePath = "steamauth.json";
        static string refreshToken;
        static string guardData;
        static string accountName;
        // True when the current login attempt used a stored refresh token rather than
        // a fresh credential authentication (controls how a failure is handled).
        static volatile bool usingStoredToken;

        private sealed class SteamAuthData
        {
            public string AccountName { get; set; }
            public string RefreshToken { get; set; }
            public string GuardData { get; set; }
        }

        static string user, pass;

        public static ConcurrentQueue<Message> Queue = new ConcurrentQueue<Message>();

        private static void Log(string msg, string level = "Info")
        {
            Console.WriteLine($"{DateTime.Now.ToString("s")}:Steam:{level}: {msg}");
        }

        private static void LoadAuth()
        {
            try
            {
                if (!File.Exists(AuthFilePath))
                    return;

                var contents = File.ReadAllText(AuthFilePath);
                if (string.IsNullOrWhiteSpace(contents) || contents == "{}")
                    return;

                var data = JsonSerializer.Deserialize<SteamAuthData>(contents);
                if (data == null)
                    return;

                accountName = data.AccountName;
                refreshToken = data.RefreshToken;
                guardData = data.GuardData;

                if (!string.IsNullOrEmpty(refreshToken))
                    Log("Loaded stored Steam refresh token; will attempt token login.");
            }
            catch (Exception ex)
            {
                Log($"Failed to load {AuthFilePath}: {ex.Message}", "Warning");
            }
        }

        private static void SaveAuth()
        {
            try
            {
                var data = new SteamAuthData
                {
                    AccountName = accountName,
                    RefreshToken = refreshToken,
                    GuardData = guardData,
                };
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

                // Guard: never overwrite a good token file with an all-empty record.
                if (string.IsNullOrEmpty(refreshToken) && string.IsNullOrEmpty(guardData) && string.IsNullOrEmpty(accountName))
                {
                    Log($"Refusing to write empty auth data to {AuthFilePath}", "Warning");
                    return;
                }

                // Atomic write with timestamped backup (same pattern as userprefs.json).
                File.WriteAllText(AuthFilePath + ".tmp", json);
                if (File.Exists(AuthFilePath))
                {
                    var backupPath = $"{AuthFilePath}.{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.bak";
                    try { File.Copy(AuthFilePath, backupPath, true); }
                    catch (Exception ex) { Log($"Failed to back up {AuthFilePath}: {ex.Message}", "Warning"); }
                }
                File.Move(AuthFilePath + ".tmp", AuthFilePath, true);
            }
            catch (Exception ex)
            {
                Log($"Failed to save {AuthFilePath}: {ex.Message}", "Warning");
            }
        }

        private static void ClearStoredToken()
        {
            // Drop the (expired) refresh token but keep guard data so the next
            // credential authentication can avoid re-prompting for a Steam Guard code.
            refreshToken = null;
            usingStoredToken = false;
            SaveAuth();
        }

        public static Task RunSteamTask()
        {
            user = ConfigurationManager.AppSettings.Get("SteamUser");
            pass = ConfigurationManager.AppSettings.Get("SteamPass");

            LoadAuth();

            return Task.Run(() =>
            {

                while (true)
                {
                    // create our steamclient instance
                    var configuration = SteamConfiguration.Create(b => b.WithProtocolTypes(ProtocolTypes.Tcp));
                    steamClient = new SteamClient(configuration);
                    // create the callback manager which will route callbacks to function calls
                    manager = new CallbackManager(steamClient);

                    // get the steamuser handler, which is used for logging on after successfully connecting
                    steamUser = steamClient.GetHandler<SteamUser>();
                    // get the steam friends handler, which is used for interacting with friends on the network after logging on
                    steamFriends = steamClient.GetHandler<SteamFriends>();

                    // register a few callbacks we're interested in
                    // these are registered upon creation to a callback manager, which will then route the callbacks
                    // to the functions specified
                    manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
                    manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

                    manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
                    manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

                    // we use the following callbacks for friends related activities
                    manager.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);
                    manager.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsList);
                    manager.Subscribe<SteamFriends.PersonaStateCallback>(OnPersonaState);
                    manager.Subscribe<SteamFriends.FriendAddedCallback>(OnFriendAdded);
                    manager.Subscribe<SteamFriends.FriendMsgCallback>(OnFriendMsg);

                    isRunning = true;

                    Log("Connecting to Steam...");

                    // initiate the connection
                    steamClient.Connect();

                    // create our callback handling loop
                    while (isRunning)
                    {
                        // in order for the callbacks to get routed, they need to be handled by the manager
                        manager.RunWaitCallbacks(TimeSpan.FromSeconds(0.1));
                    }

                    // Make sure the underlying connection is fully torn down before we
                    // build a new SteamClient on the next iteration (avoids leaking the
                    // old connection when we bailed out of the loop after a login failure).
                    try { steamClient.Disconnect(); } catch { }

                    if (permanentFailure)
                    {
                        Log("Stopping Steam reconnection due to a non-retryable login failure. Check SteamUser/SteamPass and SteamGuard settings.", "Error");
                        break;
                    }

                    errorCount++;

                    // Capped exponential backoff with jitter instead of unbounded linear growth.
                    var delay = Backoff.Compute(errorCount);
                    Log(String.Format("Reconnecting to Steam in {0:F0} seconds (attempt {1})...", delay.TotalSeconds, errorCount));
                    Thread.Sleep(delay);
                }
            });
        }

        static async void OnConnected(SteamClient.ConnectedCallback callback)
        {
            // Capture this attempt's client. The credential auth flow below can await a
            // human (a Discord Steam Guard reply) for minutes; if the CM connection drops
            // in the meantime the reconnect loop builds a brand new SteamClient. This
            // token lets a stale, still-awaiting flow detect that it has been superseded
            // and avoid mutating shared state (isRunning / LogOn) for the newer connection.
            var thisClient = steamClient;
            var thisUser = steamUser;
            try
            {
                // Fast path: reuse a stored refresh token so we don't re-run the
                // credential + Steam Guard handshake on every restart.
                if (!string.IsNullOrEmpty(refreshToken))
                {
                    usingStoredToken = true;
                    Log(String.Format("Connected to Steam! Logging in '{0}' with stored refresh token...", accountName ?? user));

                    thisUser.LogOn(new SteamUser.LogOnDetails
                    {
                        Username = accountName ?? user,
                        AccessToken = refreshToken,
                        ShouldRememberPassword = true,
                    });
                    return;
                }

                usingStoredToken = false;
                Log(String.Format("Connected to Steam! Authenticating '{0}'...", user));

                // Modern token-based authentication. Steam deprecated direct
                // username/password LogOn for non-web clients; we must obtain an
                // access/refresh token via the authentication service first.
                var authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = user,
                    Password = pass,
                    IsPersistentSession = true,
                    GuardData = guardData,
                    Authenticator = new DiscordAuthenticator(),
                });

                var pollResponse = await authSession.PollingWaitForResultAsync();

                // If a newer connection attempt superseded us while we were waiting,
                // abandon this stale flow without touching shared state.
                if (!ReferenceEquals(steamClient, thisClient))
                {
                    Log("Discarding stale Steam authentication result from a superseded connection.", "Warning");
                    return;
                }

                // Steam may hand back fresh guard data (a JWT, like the old sentry file)
                // we can reuse to avoid prompting for a code next time.
                if (!string.IsNullOrEmpty(pollResponse.NewGuardData))
                    guardData = pollResponse.NewGuardData;

                accountName = pollResponse.AccountName;
                refreshToken = pollResponse.RefreshToken;
                SaveAuth();

                // Re-check immediately before logging on in case the connection was
                // replaced during SaveAuth's file I/O.
                if (!ReferenceEquals(steamClient, thisClient))
                {
                    Log("Connection superseded before logon; discarding stale authentication.", "Warning");
                    return;
                }

                Log("Authentication succeeded; logging on...");
                thisUser.LogOn(new SteamUser.LogOnDetails
                {
                    Username = pollResponse.AccountName,
                    AccessToken = pollResponse.RefreshToken,
                    ShouldRememberPassword = true,
                });
            }
            catch (AuthenticationException aex)
            {
                if (!ReferenceEquals(steamClient, thisClient)) return; // superseded; don't disturb the newer connection
                // Credentials/handshake rejected by Steam.
                if (aex.Result == EResult.InvalidPassword)
                {
                    Log("Steam authentication failed: invalid credentials. Check SteamUser/SteamPass.", "Error");
                    permanentFailure = true;
                }
                else
                {
                    Log(String.Format("Steam authentication failed: {0} ({1}). Will retry.", aex.Message, aex.Result), "Error");
                }
                isRunning = false;
            }
            catch (TimeoutException tex)
            {
                if (!ReferenceEquals(steamClient, thisClient)) return; // superseded
                Log(String.Format("Steam authentication timed out: {0}. Will retry.", tex.Message), "Error");
                isRunning = false;
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(steamClient, thisClient)) return; // superseded
                Log(String.Format("Unexpected error during Steam authentication: {0}. Will retry.", ex.Message), "Error");
                isRunning = false;
            }
        }

        static void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            Log("Disconnected from Steam");

            isRunning = false;
        }

        static void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                // A stored refresh token was rejected — it has likely expired or been
                // revoked. Discard it and fall back to a full credential authentication
                // on the next connect (this is recoverable, not a permanent failure).
                if (usingStoredToken)
                {
                    Log(String.Format("Stored Steam refresh token rejected ({0}); will re-authenticate with credentials.", callback.Result), "Warning");
                    ClearStoredToken();
                    isRunning = false;
                    return;
                }

                if (callback.Result == EResult.AccountLogonDenied)
                {
                    // if we recieve AccountLogonDenied or one of it's flavors (AccountLogonDeniedNoMailSent, etc)
                    // then the account we're logging into is SteamGuard protected
                    // see sample 5 for how SteamGuard can be handled

                    Log("Unable to logon to Steam: This account is SteamGuard protected.", "Error");

                    permanentFailure = true;
                    isRunning = false;
                    return;
                }

                if (callback.Result == EResult.InvalidPassword)
                {
                    // Bad credentials won't fix themselves on retry — stop looping.
                    Log("Unable to logon to Steam: Invalid password. Check SteamUser/SteamPass.", "Error");

                    permanentFailure = true;
                    isRunning = false;
                    return;
                }

                Log(String.Format("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult));

                isRunning = false;
                return;
            }

            errorCount = 0;
            Log("Successfully logged on!");

            // at this point, we'd be able to perform actions on Steam

            // for this sample we wait for other callbacks to perform logic
        }

        static void OnAccountInfo(SteamUser.AccountInfoCallback callback)
        {
            // before being able to interact with friends, you must wait for the account info callback
            // this callback is posted shortly after a successful logon

            // at this point, we can go online on friends, so lets do that
            steamFriends.SetPersonaState(EPersonaState.Online);
        }

        static void OnFriendsList(SteamFriends.FriendsListCallback callback)
        {
            // at this point, the client has received it's friends list

            int friendCount = steamFriends.GetFriendCount();

            Log(String.Format("We have {0} friends", friendCount));

            for (int x = 0; x < friendCount; x++)
            {
                // steamids identify objects that exist on the steam network, such as friends, as an example
                SteamID steamIdFriend = steamFriends.GetFriendByIndex(x);

                // we'll just display the STEAM_ rendered version
                Log(String.Format("Friend: {0}", steamIdFriend.Render()));
            }

            // we can also iterate over our friendslist to accept or decline any pending invites

            foreach (var friend in callback.FriendList)
            {
                if (friend.Relationship == EFriendRelationship.RequestRecipient)
                {
                    // this user has added us, let's add him back
                    steamFriends.AddFriend(friend.SteamID);
                }
            }
        }

        static void OnFriendAdded(SteamFriends.FriendAddedCallback callback)
        {
            // someone accepted our friend request, or we accepted one
            Log(String.Format("{0} is now a friend", callback.PersonaName));
        }

        static void OnPersonaState(SteamFriends.PersonaStateCallback callback)
        {
            // this callback is received when the persona state (friend information) of a friend changes

            // for this sample we'll simply display the names of the friends
            Log(String.Format("State change: {0}", callback.Name));
        }

        static void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            Log(String.Format("Logged off of Steam: {0}", callback.Result));
        }

        static void OnFriendMsg(SteamFriends.FriendMsgCallback callback)
        {
            if (callback.EntryType == EChatEntryType.ChatMsg)
            {
                var msg = new Message() { Msg = callback.Message, Time = DateTime.UtcNow, UserId = callback.Sender, UserName = steamFriends.GetFriendPersonaName(callback.Sender) };
                Queue.Enqueue(msg);
                Log(String.Format("{0}:{1}:{2}", msg.Time.ToString("s"), msg.UserName, msg.Msg));
            }
        }
    }
}