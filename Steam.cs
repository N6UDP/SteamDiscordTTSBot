﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using SteamKit2;

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

        static bool isRunning;

        static int errorCount = 0;

        static string user, pass;

        public static ConcurrentQueue<Message> Queue = new ConcurrentQueue<Message>();

        private static void Log(string msg, string level = "Info")
        {
            Console.WriteLine($"{DateTime.Now.ToString("s")}:Steam:{level}: {msg}");
        }

        public static Task RunSteamTask()
        {
            user = ConfigurationManager.AppSettings.Get("SteamUser");
            pass = ConfigurationManager.AppSettings.Get("SteamPass");

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

                    errorCount++;

                    // Sleep for 60 seconds * errorCount
                    Log(String.Format("Sleeping for {0} minutes", errorCount));
                    Thread.Sleep((1000 * 60 * errorCount));
                }
            });
        }

        static void OnConnected(SteamClient.ConnectedCallback callback)
        {
            Log(String.Format("Connected to Steam! Logging in '{0}'...", user));

            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = user,
                Password = pass,
            });
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
                if (callback.Result == EResult.AccountLogonDenied)
                {
                    // if we recieve AccountLogonDenied or one of it's flavors (AccountLogonDeniedNoMailSent, etc)
                    // then the account we're logging into is SteamGuard protected
                    // see sample 5 for how SteamGuard can be handled

                    Log("Unable to logon to Steam: This account is SteamGuard protected.");

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