using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Lidgren.Network;
using Lidgren.Network.Xna;
using Microsoft.Xna.Framework;

namespace Infiniminer
{
    public class InfiniminerServer
    {
        InfiniminerNetServer netServer = null;
        public BlockType[, ,] blockList = null;    // In game coordinates, where Y points up.
        public Int32[, ,,] blockListContent = null;
        PlayerTeam[, ,] blockCreatorTeam = null;
        public int MAPSIZE = 64;
        Dictionary<NetConnection, Player> playerList = new Dictionary<NetConnection, Player>();
        bool sleeping = true;
        int lavaBlockCount = 0;
        int waterBlockCount = 0;
        uint oreFactor = 10;
        int frameCount = 100;
        uint prevMaxPlayers = 16;
        bool includeLava = true;
        bool includeWater = true;
        bool physicsEnabled = false;

        string levelToLoad = "";
        string greeter = "";
        List<NetConnection> toGreet = new List<NetConnection>();
        Dictionary<string, short> admins = new Dictionary<string, short>(); //Short represents power - 1 for mod, 2 for full admin

        bool[,,] tntExplosionPattern = new bool[0,0,0];
        bool announceChanges = true;

        DateTime lastServerListUpdate = DateTime.Now;
        DateTime lastMapBackup = DateTime.Now;
        List<string> banList = null;

        const int CONSOLE_SIZE = 30;
        List<string> consoleText = new List<string>();
        string consoleInput = "";

        bool keepRunning = true;

        uint teamCashRed = 0;
        uint teamCashBlue = 0;
        uint teamOreRed = 0;
        uint teamOreBlue = 0;

        uint winningCashAmount = 10000;
        PlayerTeam winningTeam = PlayerTeam.None;

        bool[, ,] flowSleep = new bool[64, 64, 64]; //if true, do not calculate this turn

        // Server restarting variables.
        DateTime restartTime = DateTime.Now;
        bool restartTriggered = false;
        
        //Variable handling
        Dictionary<string,bool> varBoolBindings = new Dictionary<string, bool>();
        Dictionary<string,string> varStringBindings = new Dictionary<string, string>();
        Dictionary<string, int> varIntBindings = new Dictionary<string, int>();
        Dictionary<string,string> varDescriptions = new Dictionary<string,string>();
        Dictionary<string, bool> varAreMessage = new Dictionary<string, bool>();

        public Vector3 Auth_Position(Vector3 pos,Player pl)//check boundaries and legality of action
        {
            BlockType testpoint = BlockAtPoint(pos);

            if (testpoint == BlockType.None || testpoint == BlockType.Vacuum || testpoint == BlockType.Water || testpoint == BlockType.Lava || testpoint == BlockType.TransBlue && pl.Team == PlayerTeam.Blue || testpoint == BlockType.TransRed && pl.Team == PlayerTeam.Red)
            {//check if player is not in wall
               //falldamage
            }
            else
            {
                if (pl.Alive)
                {
                    pl.Ore = 0;//should be calling death function for player
                    pl.Cash = 0;
                    pl.Weight = 0;
                    pl.Health = 0;
                    pl.Alive = false;

                    SendResourceUpdate(pl);
                    SendPlayerDead(pl);

                    ConsoleWrite("refused" + pl.Handle + " " + pos.X + "/" + pos.Y + "/" + pos.Z);
                    return pl.Position;
                }
                else//player is dead, return position silent
                {
                    return pl.Position;
                }
            }

            //if (Distf(pl.Position, pos) > 0.35)
            //{   //check that players last update is not further than it should be
            //    ConsoleWrite("refused" + pl.Handle + " speed:" + Distf(pl.Position, pos));
            //    //should call force update player position
            //    return pos;// pl.Position;
            //}
            //else
            //{
            //    return pos;
            //}

            return pos;
        }
        public Vector3 Auth_Heading(Vector3 head)//check boundaries and legality of action
        {
            return head;
        }

        public void varBindingsInitialize()
        {
            //Bool bindings
            varBind("tnt", "TNT explosions", false, true);
            varBind("stnt", "Spherical TNT explosions", true, true);
            varBind("sspreads", "Lava spreading via shock blocks", true, false);
            varBind("roadabsorbs", "Letting road blocks above lava absorb it", true, false);
            varBind("insane", "Insane liquid spreading, so as to fill any hole", false, false);
            varBind("minelava", "Lava pickaxe mining", true, false);
            //***New***
            varBind("public", "Server publicity", true, false);
            varBind("sandbox", "Sandbox mode", true, false);
            //Announcing is a special case, as it will never announce for key name announcechanges
            varBind("announcechanges", "Toggles variable changes being announced to clients", true, false);

            //String bindings
            varBind("name", "Server name as it appears on the server browser", "Unnamed Server");
            varBind("greeter", "The message sent to new players", "");

            //Int bindings
            varBind("maxplayers", "Maximum player count", 16);
            varBind("explosionradius", "The radius of spherical tnt explosions", 3);
        }

        public void varBind(string name, string desc, bool initVal, bool useAre)
        {
            varBoolBindings[name] = initVal;
            varDescriptions[name] = desc;
            /*if (varBoolBindings.ContainsKey(name))
                varBoolBindings[name] = initVal;
            else
                varBoolBindings.Add(name, initVal);

            if (varDescriptions.ContainsKey(name))
                varDescriptions[name] = desc;
            else
                varDescriptions.Add(name, desc);*/

            varAreMessage[name] = useAre;
        }

        public void varBind(string name, string desc, string initVal)
        {
            varStringBindings[name] = initVal;
            varDescriptions[name] = desc;
            /*
            if (varStringBindings.ContainsKey(name))
                varStringBindings[name] = initVal;
            else
                varStringBindings.Add(name, initVal);

            if (varDescriptions.ContainsKey(name))
                varDescriptions[name] = desc;
            else
                varDescriptions.Add(name, desc);*/
        }

        public void varBind(string name, string desc, int initVal)
        {
            varIntBindings[name] = initVal;
            varDescriptions[name] = desc;
            /*if (varDescriptions.ContainsKey(name))
                varDescriptions[name] = desc;
            else
                varDescriptions.Add(name, desc);*/
        }

        public bool varChangeCheckSpecial(string name)
        {
            switch (name)
            {
                case "maxplayers":
                    //Check if smaller than player count
                    if (varGetI(name) < playerList.Count)
                    {
                        //Bail, set to previous value
                        varSet(name, (int)prevMaxPlayers,true);
                        return false;
                    }
                    else
                    {
                        prevMaxPlayers = (uint)varGetI(name);
                        netServer.Configuration.MaxConnections = varGetI(name);
                    }
                    break;
                case "explosionradius":
                    CalculateExplosionPattern();
                    break;
                case "greeter":
                    /*PropertyBag _P = new PropertyBag(new InfiniminerGame(new string[]{}));
                    string[] format = _P.ApplyWordrwap(varGetS("greeter"));
                    */
                    greeter = varGetS("greeter");
                    break;
            }
            return true;
        }

        public bool varGetB(string name)
        {
            if (varBoolBindings.ContainsKey(name) && varDescriptions.ContainsKey(name))
                return varBoolBindings[name];
            else
                return false;
        }

        public string varGetS(string name)
        {
            if (varStringBindings.ContainsKey(name) && varDescriptions.ContainsKey(name))
                return varStringBindings[name];
            else
                return "";
        }

        public int varGetI(string name)
        {
            if (varIntBindings.ContainsKey(name) && varDescriptions.ContainsKey(name))
                return varIntBindings[name];
            else
                return -1;
        }

        public int varExists(string name)
        {
            if (varDescriptions.ContainsKey(name))
                if (varBoolBindings.ContainsKey(name))
                    return 1;
                else if (varStringBindings.ContainsKey(name))
                    return 2;
                else if (varIntBindings.ContainsKey(name))
                    return 3;
            return 0;
        }

        public void varSet(string name, bool val)
        {
            varSet(name, val, false);
        }

        public void varSet(string name, bool val, bool silent)
        {
            if (varBoolBindings.ContainsKey(name) && varDescriptions.ContainsKey(name))
            {
                varBoolBindings[name] = val;
                string enabled = val ? "enabled!" : "disabled.";
                if (name!="announcechanges"&&!silent)
                    MessageAll(varDescriptions[name] + (varAreMessage[name] ? " are " + enabled : " is " + enabled));
                if (!silent)
                {
                    varReportStatus(name, false);
                    varChangeCheckSpecial(name);
                }
            }
            else
                ConsoleWrite("Variable \"" + name + "\" does not exist!");
        }

        public void varSet(string name, string val)
        {
            varSet(name, val, false);
        }

        public void varSet(string name, string val, bool silent)
        {
            if (varStringBindings.ContainsKey(name) && varDescriptions.ContainsKey(name))
            {
                varStringBindings[name] = val;
                if (!silent)
                {
                    varReportStatus(name);
                    varChangeCheckSpecial(name);
                }
            }
            else
                ConsoleWrite("Variable \"" + name + "\" does not exist!");
        }

        public void varSet(string name, int val)
        {
            varSet(name, val, false);
        }

        public void varSet(string name, int val, bool silent)
        {
            if (varIntBindings.ContainsKey(name) && varDescriptions.ContainsKey(name))
            {
                varIntBindings[name] = val;
                if (!silent)
                {
                    MessageAll(name + " = " + val.ToString());
                    varChangeCheckSpecial(name);
                }
            }
            else
                ConsoleWrite("Variable \"" + name + "\" does not exist!");
        }

        public string varList()
        {
            return varList(false);
        }

        private void varListType(ICollection<string> keys, string naming)
        {
            
            const int lineLength = 3;
            if (keys.Count > 0)
            {
                ConsoleWrite(naming);
                int i = 1;
                string output = "";
                foreach (string key in keys)
                {
                    if (i == 1)
                    {
                        output += "\t" + key;
                    }
                    else if (i >= lineLength)
                    {
                        output += ", " + key;
                        ConsoleWrite(output);
                        output = "";
                        i = 0;
                    }
                    else
                    {
                        output += ", " + key;
                    }
                    i++;
                }
                if (i > 1)
                    ConsoleWrite(output);
            }
        }

        public string varList(bool autoOut)
        {
            if (!autoOut)
            {
                string output = "";
                int i = 0;
                foreach (string key in varBoolBindings.Keys)
                {
                    if (i == 0)
                        output += key;
                    else
                        output += "," + key;
                    i++;
                }
                foreach (string key in varStringBindings.Keys)
                {
                    if (i == 0)
                        output += "s " + key;
                    else
                        output += ",s " + key;
                    i++;
                }
                return output;
            }
            else
            {
                varListType((ICollection<string>)varBoolBindings.Keys, "Boolean Vars:");
                varListType((ICollection<string>)varStringBindings.Keys, "String Vars:");
                varListType((ICollection<string>)varIntBindings.Keys, "Int Vars:");

                /*ConsoleWrite("String count: " + varStringBindings.Keys.Count);
                outt = new string[varStringBindings.Keys.Count];
                varStringBindings.Keys.CopyTo(outt, 0);
                varListType(outt, "String Vars:");

                ConsoleWrite("Int count: " + varIntBindings.Keys.Count);
                outt = new string[varIntBindings.Keys.Count];
                varIntBindings.Keys.CopyTo(outt, 0);
                varListType(outt, "Integer Vars:");*/
                /*if (varStringBindings.Count > 0)
                {
                    ConsoleWrite("String Vars:");
                    int i = 1;
                    string output = "";
                    foreach (string key in varStringBindings.Keys)
                    {
                        if (i == 1)
                        {
                            output += key;
                        }
                        else if (i >= lineLength)
                        {
                            output += "," + key;
                            ConsoleWrite(output);
                            output = "";
                        }
                        else
                        {
                            output += "," + key;
                        }
                        i++;
                    }
                }
                if (varIntBindings.Count > 0)
                {
                    ConsoleWrite("Integer Vars:");
                    int i = 1;
                    string output = "";
                    foreach (string key in varIntBindings.Keys)
                    {
                        if (i == 1)
                        {
                            output += "\t"+key;
                        }
                        else if (i >= lineLength)
                        {
                            output += "," + key;
                            ConsoleWrite(output);
                            output = "";
                        }
                        else
                        {
                            output += "," + key;
                        }
                        i++;
                    }
                }*/
                return "";
            }
        }

        public void varReportStatus(string name)
        {
            varReportStatus(name, true);
        }

        public void varReportStatus(string name, bool full)
        {
            if (varDescriptions.ContainsKey(name))
            {
                if (varBoolBindings.ContainsKey(name))
                {
                    ConsoleWrite(name + " = " + varBoolBindings[name].ToString());
                    if (full)
                        ConsoleWrite(varDescriptions[name]);
                    return;
                }
                else if (varStringBindings.ContainsKey(name))
                {
                    ConsoleWrite(name + " = " + varStringBindings[name]);
                    if (full)
                        ConsoleWrite(varDescriptions[name]);
                    return;
                }
                else if (varIntBindings.ContainsKey(name))
                {
                    ConsoleWrite(name + " = " + varIntBindings[name]);
                    if (full)
                        ConsoleWrite(varDescriptions[name]);
                    return;
                }
            }
            ConsoleWrite("Variable \"" + name + "\" does not exist!");
        }

        public string varReportStatusString(string name, bool full)
        {
            if (varDescriptions.ContainsKey(name))
            {
                if (varBoolBindings.ContainsKey(name))
                {
                    return name + " = " + varBoolBindings[name].ToString();
                }
                else if (varStringBindings.ContainsKey(name))
                {
                    return name + " = " + varStringBindings[name];
                }
                else if (varIntBindings.ContainsKey(name))
                {
                    return name + " = " + varIntBindings[name];
                }
            }
            return "";
        }

        public InfiniminerServer()
        {
            Console.SetWindowSize(1, 1);
            Console.SetBufferSize(80, CONSOLE_SIZE + 4);
            Console.SetWindowSize(80, CONSOLE_SIZE + 4);
        }

        public string GetExtraInfo()
        {
            string extraInfo = "";
            if (varGetB("sandbox"))
                extraInfo += "sandbox";
            else
                extraInfo += string.Format("{0:#.##k}", winningCashAmount / 1000);
            if (!includeLava)
                extraInfo += ", !lava";
            if (!includeWater)
                extraInfo += ", !water";
            if (!varGetB("tnt"))
                extraInfo += ", !tnt";
            if (varGetB("insane") || varGetB("sspreads") || varGetB("stnt"))
                extraInfo += ", insane";

/*            if (varGetB("insanelava"))//insaneLava)
                extraInfo += ", ~lava";
            if (varGetB("sspreads"))
                extraInfo += ", shock->lava";
            if (varGetB("stnt"))//sphericalTnt && false)
                extraInfo += ", stnt";*/
            return extraInfo;
        }

        public void PublicServerListUpdate()
        {
            PublicServerListUpdate(false);
        }

        public void PublicServerListUpdate(bool doIt)
        {
            if (!varGetB("public"))
                return;

            TimeSpan updateTimeSpan = DateTime.Now - lastServerListUpdate;
            if (updateTimeSpan.TotalMinutes >= 1 || doIt)
                CommitUpdate();
        }

        public bool ProcessCommand(string chat)
        {
            return ProcessCommand(chat, (short)1, null);
        }

        public bool ProcessCommand(string input, short authority, Player sender)
        {
            if (authority == 0)
                return false;
            if (sender != null)
                sender.admin = GetAdmin(sender.IP);
            string[] args = input.Split(' '.ToString().ToCharArray(),2);
            if (args[0].StartsWith("\\") && args[0].Length > 1)
                args[0] = args[0].Substring(1);
            switch (args[0].ToLower())
            {
                case "help":
                    {
                        if (sender == null)
                        {
                            ConsoleWrite("SERVER CONSOLE COMMANDS:");
                            ConsoleWrite(" fps");
                            ConsoleWrite(" physics");
                            ConsoleWrite(" announce");
                            ConsoleWrite(" players");
                            ConsoleWrite(" kick <ip>");
                            ConsoleWrite(" kickn <name>");
                            ConsoleWrite(" ban <ip>");
                            ConsoleWrite(" bann <name>");
                            ConsoleWrite(" say <message>");
                            ConsoleWrite(" save <mapfile>");
                            ConsoleWrite(" load <mapfile>");
                            ConsoleWrite(" toggle <var>");
                            ConsoleWrite(" <var> <value>");
                            ConsoleWrite(" <var>");
                            ConsoleWrite(" listvars");
                            ConsoleWrite(" status");
                            ConsoleWrite(" restart");
                            ConsoleWrite(" quit");
                        }
                        else
                        {
                            SendServerMessageToPlayer(sender.Handle + ", the " + args[0].ToLower() + " command is only for use in the server console.", sender.NetConn);
                        }
                    }
                    break;
                case "players":
                    {
                        if (sender == null)
                        {
                            ConsoleWrite("( " + playerList.Count + " / " + varGetI("maxplayers") + " )");
                            foreach (Player p in playerList.Values)
                            {
                                string teamIdent = "";
                                if (p.Team == PlayerTeam.Red)
                                    teamIdent = " (R)";
                                else if (p.Team == PlayerTeam.Blue)
                                    teamIdent = " (B)";
                                if (p.IsAdmin)
                                    teamIdent += " (Admin)";
                                ConsoleWrite(p.Handle + teamIdent);
                                ConsoleWrite("  - " + p.IP);
                            }
                        }else{
                            SendServerMessageToPlayer(sender.Handle + ", the " + args[0].ToLower() + " command is only for use in the server console.", sender.NetConn);
                        }
                    }
                    break;
                case "fps":
                    {
                        ConsoleWrite("Server FPS:"+frameCount );
                    }
                    break;
                case "physics":
                    {
                        physicsEnabled = !physicsEnabled;
                        ConsoleWrite("Physics state is now: " + physicsEnabled);
                    }
                    break;
                case "liquid":
                    {
                        lavaBlockCount = 0;
                        waterBlockCount = 0;

                        for (ushort i = 0; i < MAPSIZE; i++)
                            for (ushort j = 0; j < MAPSIZE; j++)
                                for (ushort k = 0; k < MAPSIZE; k++)
                                {
                                    if (blockList[i,j,k] == BlockType.Lava)
                                    {
                                        lavaBlockCount += 1;
                                    }
                                    else if (blockList[i, j, k] == BlockType.Water)
                                    {
                                        waterBlockCount += 1;
                                    }
                                }

                        ConsoleWrite(waterBlockCount + " water blocks, " + lavaBlockCount + " lava blocks.");
                    }
                    break;
                case "flowsleep":
                    {
                        uint sleepcount = 0;

                        for (ushort i = 0; i < MAPSIZE; i++)
                            for (ushort j = 0; j < MAPSIZE; j++)
                                for (ushort k = 0; k < MAPSIZE; k++)
                                    if (flowSleep[i, j, k] == true)
                                        sleepcount += 1;

                        ConsoleWrite(sleepcount +" liquids are happily sleeping.");
                    }
                    break;
                case "admins":
                    {
                        ConsoleWrite("Admin list:");
                        foreach (string ip in admins.Keys)
                            ConsoleWrite(ip);
                    }
                    break;
                case "admin":
                    {
                        if (args.Length == 2)
                        {
                            if (sender == null || sender.admin >= 2)
                                AdminPlayer(args[1]);
                            else
                                SendServerMessageToPlayer("You do not have the authority to add admins.", sender.NetConn);
                        }
                    }
                    break;
                case "adminn":
                    {
                        if (args.Length == 2)
                        {
                            if (sender == null || sender.admin >= 2)
                                AdminPlayer(args[1],true);
                            else
                                SendServerMessageToPlayer("You do not have the authority to add admins.", sender.NetConn);
                        }
                    }
                    break;
                case "listvars":
                    if (sender==null)
                        varList(true);
                    else{
                        SendServerMessageToPlayer(sender.Handle + ", the " + args[0].ToLower() + " command is only for use in the server console.", sender.NetConn);
                    }
                    break;
                case "status":
                    if (sender == null)
                        status();
                    else
                        SendServerMessageToPlayer(sender.Handle + ", the " + args[0].ToLower() + " command is only for use in the server console.", sender.NetConn);
                    break;
                case "announce":
                    {
                        PublicServerListUpdate(true);
                    }
                    break;
                case "kick":
                    {
                        if (authority>=1&&args.Length == 2)
                        {
                            if (sender != null)
                                ConsoleWrite("SERVER: " + sender.Handle + " has kicked " + args[1]);
                            KickPlayer(args[1]);
                        }
                    }
                    break;
                case "kickn":
                    {
                        if (authority >= 1 && args.Length == 2)
                        {
                            if (sender != null)
                                ConsoleWrite("SERVER: " + sender.Handle + " has kicked " + args[1]);
                            KickPlayer(args[1], true);
                        }
                    }
                    break;

                case "ban":
                    {
                        if (authority >= 1 && args.Length == 2)
                        {
                            if (sender != null)
                                ConsoleWrite("SERVER: " + sender.Handle + " has banned " + args[1]);
                            BanPlayer(args[1]);
                            KickPlayer(args[1]);
                        }
                    }
                    break;

                case "bann":
                    {
                        if (authority >= 1 && args.Length == 2)
                        {
                            if (sender != null)
                                ConsoleWrite("SERVER: " + sender.Handle + " has bannec " + args[1]);
                            BanPlayer(args[1], true);
                            KickPlayer(args[1], true);
                        }
                    }
                    break;

                case "toggle":
                    if (authority >= 1 && args.Length == 2)
                    {
                        int exists = varExists(args[1]);
                        if (exists == 1)
                        {
                            bool val = varGetB(args[1]);
                            varSet(args[1], !val);
                        }
                        else if (exists == 2)
                            ConsoleWrite("Cannot toggle a string value.");
                        else
                            varReportStatus(args[1]);
                    }
                    else
                        ConsoleWrite("Need variable name to toggle!");
                    break;
                case "quit":
                    {
                        if (authority >= 2){
                            if ( sender!=null)
                                ConsoleWrite(sender.Handle + " is shutting down the server.");
                             keepRunning = false;
                        }
                    }
                    break;

                case "restart":
                    {
                        if (authority >= 2){
                            if (sender != null)
                                ConsoleWrite(sender.Handle + " is restarting the server.");
                            else
                            {
                                ConsoleWrite("Restarting server in 5 seconds.");
                            }
                            //disconnectAll();
                           
                            SendServerMessage("Server restarting in 5 seconds.");
                            restartTriggered = true;
                            restartTime = DateTime.Now+TimeSpan.FromSeconds(5);
                        }
                    }
                    break;

                case "say":
                    {
                        if (args.Length == 2)
                        {
                            string message = "SERVER: " + args[1];
                            SendServerMessage(message);
                        }
                    }
                    break;

                case "save":
                    {
                        if (args.Length >= 2)
                        {
                            if (sender!=null)
                                ConsoleWrite(sender.Handle + " is saving the map.");
                            SaveLevel(args[1]);
                        }
                    }
                    break;

                case "load":
                    {
                        if (args.Length >= 2)
                        {
                            if (sender!=null)
                                ConsoleWrite(sender.Handle + " is loading a map.");
                            physicsEnabled = false;
                            Thread.Sleep(2);
                            LoadLevel(args[1]);
                            physicsEnabled = true;
                            /*if (LoadLevel(args[1]))
                                Console.WriteLine("Loaded level " + args[1]);
                            else
                                Console.WriteLine("Level file not found!");*/
                        }
                        else if (levelToLoad != "")
                        {
                            physicsEnabled = false;
                            Thread.Sleep(2);
                            LoadLevel(levelToLoad);
                            physicsEnabled = true;
                        }
                    }
                    break;
                default: //Check / set var
                    {
                        string name = args[0];
                        int exists = varExists(name);
                        if (exists > 0)
                        {
                            if (args.Length == 2)
                            {
                                try
                                {
                                    if (exists == 1)
                                    {
                                        bool newVal = false;
                                        newVal = bool.Parse(args[1]);
                                        varSet(name, newVal);
                                    }
                                    else if (exists == 2)
                                    {
                                        varSet(name, args[1]);
                                    }
                                    else if (exists == 3)
                                    {
                                        varSet(name, Int32.Parse(args[1]));
                                    }

                                }
                                catch { }
                            }
                            else
                            {
                                if (sender==null)
                                    varReportStatus(name);
                                else
                                    SendServerMessageToPlayer(sender.Handle + ": The " + args[0].ToLower() + " command is only for use in the server console.",sender.NetConn);
                            }
                        }
                        else
                        {
                            char first = args[0].ToCharArray()[0];
                            if (first == 'y' || first == 'Y')
                            {
                                string message = "SERVER: " + args[0].Substring(1);
                                if (args.Length > 1)
                                    message += (message != "SERVER: " ? " " : "") + args[1];
                                SendServerMessage(message);
                            }
                            else
                            {
                                if (sender == null)
                                    ConsoleWrite("Unknown command/var.");
                                return false;
                            }
                        }
                    }
                    break;
            }
            return true;
        }

        public void MessageAll(string text)
        {
            if (announceChanges)
                SendServerMessage(text);
            ConsoleWrite(text);
        }

        public void ConsoleWrite(string text)
        {
            consoleText.Add(text);
            if (consoleText.Count > CONSOLE_SIZE)
                consoleText.RemoveAt(0);
            ConsoleRedraw();
        }

        public Dictionary<string, short> LoadAdminList()
        {
            Dictionary<string, short> temp = new Dictionary<string, short>();

            try
            {
                if (!File.Exists("admins.txt"))
                {
                    FileStream fs = File.Create("admins.txt");
                    StreamWriter sr = new StreamWriter(fs);
                    sr.WriteLine("#A list of all admins - just add one ip per line");
                    sr.Close();
                    fs.Close();
                }
                else
                {
                    FileStream file = new FileStream("admins.txt", FileMode.Open, FileAccess.Read);
                    StreamReader sr = new StreamReader(file);
                    string line = sr.ReadLine();
                    while (line != null)
                    {
                        if (line.Trim().Length!=0&&line.Trim().ToCharArray()[0]!='#')
                            temp.Add(line.Trim(), (short)2); //This will be changed to note authority too
                        line = sr.ReadLine();
                    }
                    sr.Close();
                    file.Close();
                }
            }
            catch {
                ConsoleWrite("Unable to load admin list.");
            }

            return temp;
        }

        public bool SaveAdminList()
        {
            try
            {
                FileStream file = new FileStream("admins.txt", FileMode.Create, FileAccess.Write);
                StreamWriter sw = new StreamWriter(file);
                sw.WriteLine("#A list of all admins - just add one ip per line\n");
                foreach (string ip in banList)
                    sw.WriteLine(ip);
                sw.Close();
                file.Close();
                return true;
            }
            catch { }
            return false;
        }

        public List<string> LoadBanList()
        {
            List<string> retList = new List<string>();

            try
            {
                FileStream file = new FileStream("banlist.txt", FileMode.Open, FileAccess.Read);
                StreamReader sr = new StreamReader(file);
                string line = sr.ReadLine();
                while (line != null)
                {
                    retList.Add(line.Trim());
                    line = sr.ReadLine();
                }
                sr.Close();
                file.Close();
            }
            catch { }

            return retList;
        }

        public void SaveBanList(List<string> banList)
        {
            try
            {
                FileStream file = new FileStream("banlist.txt", FileMode.Create, FileAccess.Write);
                StreamWriter sw = new StreamWriter(file);
                foreach (string ip in banList)
                    sw.WriteLine(ip);
                sw.Close();
                file.Close();
            }
            catch { }
        }

        public void KickPlayer(string ip)
        {
            KickPlayer(ip, false);
        }

        public void KickPlayer(string ip, bool name)
        {
            List<Player> playersToKick = new List<Player>();
            foreach (Player p in playerList.Values)
            {
                if ((p.IP == ip && !name) || (p.Handle.ToLower().Contains(ip.ToLower()) && name))
                    playersToKick.Add(p);
            }
            foreach (Player p in playersToKick)
            {
                p.NetConn.Disconnect("", 0);
                p.Kicked = true;
            }
        }

        public void BanPlayer(string ip)
        {
            BanPlayer(ip, false);
        }

        public void BanPlayer(string ip, bool name)
        {
            string realIp = ip;
            if (name)
            {
                foreach (Player p in playerList.Values)
                {
                    if ((p.Handle == ip && !name) || (p.Handle.ToLower().Contains(ip.ToLower()) && name))
                    {
                        realIp = p.IP;
                        break;
                    }
                }
            }
            if (!banList.Contains(realIp))
            {
                banList.Add(realIp);
                SaveBanList(banList);
            }
        }

        public short GetAdmin(string ip)
        {
            if (admins.ContainsKey(ip.Trim()))
                return admins[ip.Trim()];
            return (short)0;
        }

        public void AdminPlayer(string ip)
        {
            AdminPlayer(ip, false,(short)2);
        }

        public void AdminPlayer(string ip, bool name)
        {
            AdminPlayer(ip, name, (short)2);
        }

        public void AdminPlayer(string ip, bool name, short authority)
        {
            string realIp = ip;
            if (name)
            {
                foreach (Player p in playerList.Values)
                {
                    if ((p.Handle == ip && !name) || (p.Handle.ToLower().Contains(ip.ToLower()) && name))
                    {
                        realIp = p.IP;
                        break;
                    }
                }
            }
            if (!admins.ContainsKey(realIp))
            {
                admins.Add(realIp,authority);
                SaveAdminList();
            }
        }

        public void ConsoleProcessInput()
        {
            ConsoleWrite("> " + consoleInput);
            
            ProcessCommand(consoleInput, (short)2, null);
            /*string[] args = consoleInput.Split(" ".ToCharArray(),2);

            
            switch (args[0].ToLower().Trim())
            {
                case "help":
                    {
                        ConsoleWrite("SERVER CONSOLE COMMANDS:");
                        ConsoleWrite(" announce");
                        ConsoleWrite(" players");
                        ConsoleWrite(" kick <ip>");
                        ConsoleWrite(" kickn <name>");
                        ConsoleWrite(" ban <ip>");
                        ConsoleWrite(" bann <name>");
                        ConsoleWrite(" say <message>");
                        ConsoleWrite(" save <mapfile>");
                        ConsoleWrite(" load <mapfile>");
                        ConsoleWrite(" toggle <var>");//ConsoleWrite(" toggle [" + varList() + "]");//[tnt,stnt,sspreads,insanelava,minelava,announcechanges]");
                        ConsoleWrite(" <var> <value>");
                        ConsoleWrite(" <var>");
                        ConsoleWrite(" listvars");
                        ConsoleWrite(" status");
                        ConsoleWrite(" restart");
                        //ConsoleWrite(" reload");
                        ConsoleWrite(" quit");
                    }
                    break;
                case "players":
                    {
                        ConsoleWrite("( " + playerList.Count + " / " + varGetI("maxplayers") + " )");//maxPlayers + " )");
                        foreach (Player p in playerList.Values)
                        {
                            string teamIdent = "";
                            if (p.Team == PlayerTeam.Red)
                                teamIdent = " (R)";
                            else if (p.Team == PlayerTeam.Blue)
                                teamIdent = " (B)";
                            ConsoleWrite(p.Handle + teamIdent);
                            ConsoleWrite("  - " + p.IP);
                        }
                    }
                    break;
                case "listvars":
                    varList(true);
                    break;
                case "announce":
                    {
                        PublicServerListUpdate(true);
                    }
                    break;
                case "kick":
                    {
                        if (args.Length == 2)
                        {
                            KickPlayer(args[1]);
                        }
                    }
                    break;
                case "kickn":
                    {
                        if (args.Length == 2)
                        {
                            KickPlayer(args[1], true);
                        }
                    }
                    break;

                case "ban":
                    {
                        if (args.Length == 2)
                        {
                            KickPlayer(args[1]);
                            BanPlayer(args[1]);
                        }
                    }
                    break;

                case "bann":
                    {
                        if (args.Length == 2)
                        {
                            KickPlayer(args[1],true);
                            BanPlayer(args[1],true);
                        }
                    }
                    break;

                case "toggle":
                    if (args.Length == 2)
                    {
                        int exists = varExists(args[1]);
                        if (exists == 1)
                        {
                            bool val = varGetB(args[1]);
                            varSet(args[1], !val);
                        }
                        else if (exists == 2)
                            ConsoleWrite("Cannot toggle a string value.");
                        else
                            varReportStatus(args[1]);
                    }
                    else
                        ConsoleWrite("Need variable name to toggle!");
                    break;
                case "quit":
                    {
                        keepRunning = false;
                    }
                    break;

                case "restart":
                    {
                        disconnectAll();
                        restartTriggered = true;
                        restartTime = DateTime.Now;
                    }
                    break;

                case "say":
                    {
                        if (args.Length == 2)
                        {
                            string message = "SERVER: " + args[1];
                            SendServerMessage(message);
                        }
                    }
                    break;

                case "save":
                    {
                        if (args.Length >= 2)
                        {
                            SaveLevel(args[1]);
                        }
                    }
                    break;

                case "load":
                    {
                        if (args.Length >= 2)
                        {
                            LoadLevel(args[1]);
                        }
                        else if (levelToLoad != "")
                        {
                            LoadLevel(levelToLoad);
                        }
                    }
                    break;
                case "status":
                    status();
                    break;
                default: //Check / set var
                    {
                        string name = args[0];
                        int exists = varExists(name);
                        if (exists > 0)
                        {
                            if (args.Length == 2)
                            {
                                try
                                {
                                    if (exists == 1)
                                    {
                                        bool newVal = false;
                                        newVal = bool.Parse(args[1]);
                                        varSet(name, newVal);
                                    }
                                    else if (exists == 2)
                                    {
                                        varSet(name, args[1]);
                                    }
                                    else if (exists == 3)
                                    {
                                        varSet(name, Int32.Parse(args[1]));
                                    }

                                }
                                catch { }
                            }
                            else
                            {
                                varReportStatus(name);
                            }
                        }
                        else
                        {
                            char first=args[0].ToCharArray()[0];
                            if (first == 'y' || first == 'Y')
                            {
                                string message = "SERVER: " + args[0].Substring(1);
                                if (args.Length > 1)
                                    message += (message!="SERVER: " ? " " : "") + args[1];
                                SendServerMessage(message);
                            }
                            else
                                ConsoleWrite("Unknown command/var.");
                        }
                    }
                    break;
            }*/

            consoleInput = "";
            ConsoleRedraw();
        }

        public void SaveLevel(string filename)
        {
            FileStream fs = new FileStream(filename, FileMode.Create);
            StreamWriter sw = new StreamWriter(fs);
            for (int x = 0; x < MAPSIZE; x++)
                for (int y = 0; y < MAPSIZE; y++)
                    for (int z = 0; z < MAPSIZE; z++)
                        sw.WriteLine((byte)blockList[x, y, z] + "," + (byte)blockCreatorTeam[x, y, z]);
            sw.Close();
            fs.Close();
        }

        public bool LoadLevel(string filename)
        {
            try
            {
                if (!File.Exists(filename))
                {
                    ConsoleWrite("Unable to load level - " + filename + " does not exist!");
                    return false;
                }
                SendServerMessage("Changing map to " + filename + "!");
                disconnectAll();
                
                FileStream fs = new FileStream(filename, FileMode.Open);
                StreamReader sr = new StreamReader(fs);
                for (int x = 0; x < MAPSIZE; x++)
                    for (int y = 0; y < MAPSIZE; y++)
                        for (int z = 0; z < MAPSIZE; z++)
                        {
                            string line = sr.ReadLine();
                            string[] fileArgs = line.Split(",".ToCharArray());
                            if (fileArgs.Length == 2)
                            {
                                blockList[x, y, z] = (BlockType)int.Parse(fileArgs[0], System.Globalization.CultureInfo.InvariantCulture);
                                blockCreatorTeam[x, y, z] = (PlayerTeam)int.Parse(fileArgs[1], System.Globalization.CultureInfo.InvariantCulture);
                            }
                        }
                sr.Close();
                fs.Close();
                for (ushort i = 0; i < MAPSIZE; i++)
                    for (ushort j = 0; j < MAPSIZE; j++)
                        for (ushort k = 0; k < MAPSIZE; k++)
                            flowSleep[i, j, k] = false;
                ConsoleWrite("Level loaded successfully - now playing " + filename + "!");
                return true;
            }
            catch { }
            return false;
        }

        public void ResetLevel()
        {
            disconnectAll();
            newMap();
        }

        public void disconnectAll()
        {
            foreach (Player p in playerList.Values)
            {
                p.NetConn.Disconnect("",0);  
            }
        }

        public void ConsoleRedraw()
        {
            Console.Clear();
            ConsoleDrawCentered("INFINIMINER SERVER " + Defines.INFINIMINER_VERSION, 0);
            ConsoleDraw("================================================================================", 0, 1);
            for (int i = 0; i < consoleText.Count; i++)
                ConsoleDraw(consoleText[i], 0, i + 2);
            ConsoleDraw("================================================================================", 0, CONSOLE_SIZE + 2);
            ConsoleDraw("> " + consoleInput, 0, CONSOLE_SIZE + 3);
        }

        public void ConsoleDraw(string text, int x, int y)
        {
            Console.SetCursorPosition(x, y);
            Console.Write(text);
        }

        public void ConsoleDrawCentered(string text, int y)
        {
            Console.SetCursorPosition(40 - text.Length / 2, y);
            Console.Write(text);
        }

        List<string> beaconIDList = new List<string>();
        Dictionary<Vector3, Beacon> beaconList = new Dictionary<Vector3, Beacon>();
        List<string> itemIDList = new List<string>();
        Dictionary<string, Item> itemList = new Dictionary<string, Item>();

        Random randGen = new Random();
        public string _GenerateBeaconID()
        {
            string id = "K";
            for (int i = 0; i < 3; i++)
                id += (char)randGen.Next(48, 58);
            return id;
        }
        public string GenerateBeaconID()
        {
            string newId = _GenerateBeaconID();
            while (beaconIDList.Contains(newId))
                newId = _GenerateBeaconID();
            beaconIDList.Add(newId);
            return newId;
        }

        public string _GenerateItemID()
        {
            string id = "K";
            for (int i = 0; i < 3; i++)
                id += (char)randGen.Next(48, 58);
            return id;
        }
        public string GenerateItemID()
        {
            string newId = _GenerateItemID();
            while (itemIDList.Contains(newId))
                newId = _GenerateItemID();
            itemIDList.Add(newId);
            return newId;
        }
        public void SetItem(Vector3 pos, Vector3 heading, PlayerTeam team)
        {
                Item newItem = new Item(null);
                newItem.ID = GenerateItemID();
                newItem.Team = team;
                newItem.Heading = heading;
                newItem.Position = pos;
                itemList[newItem.ID] = newItem;
                SendSetItem(newItem.ID, newItem.Position, newItem.Team, newItem.Heading);
        }
        public void SetBlock(ushort x, ushort y, ushort z, BlockType blockType, PlayerTeam team)
        {
            if (x <= 0 || y <= 0 || z <= 0 || (int)x > MAPSIZE - 1 || (int)y > MAPSIZE - 1 || (int)z > MAPSIZE - 1)
                return;

            if (blockType == BlockType.None)//block removed, we must unsleep liquids nearby
            {
                Disturb(x, y, z);
            }
            if (blockType == BlockType.BeaconRed || blockType == BlockType.BeaconBlue)
            {
                Beacon newBeacon = new Beacon();
                newBeacon.ID = GenerateBeaconID();
                newBeacon.Team = blockType == BlockType.BeaconRed ? PlayerTeam.Red : PlayerTeam.Blue;
                beaconList[new Vector3(x, y, z)] = newBeacon;
                SendSetBeacon(new Vector3(x, y+1, z), newBeacon.ID, newBeacon.Team);
            }
            else if(blockType == BlockType.Pipe)
            {
                blockListContent[x, y, z, 0] = 0;//state
                blockListContent[x, y, z, 1] = 0;
                blockListContent[x, y, z, 2] = 0;
                blockListContent[x, y, z, 3] = 0;
            }
            else if (blockType == BlockType.Pump)
            {
                blockListContent[x, y, z, 0] = 0;//state
                blockListContent[x, y, z, 1] = 0;//direction
                blockListContent[x, y, z, 2] = 0;//x input
                blockListContent[x, y, z, 3] = -1;//y input
                blockListContent[x, y, z, 4] = 0;//z input
                blockListContent[x, y, z, 5] = 0;//x output
                blockListContent[x, y, z, 6] = 1;//y output
                blockListContent[x, y, z, 7] = 0;//z output
            }

            if (blockType == BlockType.None && (blockList[x, y, z] == BlockType.BeaconRed || blockList[x, y, z] == BlockType.BeaconBlue))
            {
                if (beaconList.ContainsKey(new Vector3(x,y,z)))
                    beaconList.Remove(new Vector3(x,y,z));
                SendSetBeacon(new Vector3(x, y+1, z), "", PlayerTeam.None);
            }
            
            blockList[x, y, z] = blockType;
            blockCreatorTeam[x, y, z] = team;
            flowSleep[x, y, z] = false;

            // x, y, z, type, all bytes
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.BlockSet);
            msgBuffer.Write((byte)x);
            msgBuffer.Write((byte)y);
            msgBuffer.Write((byte)z);
            if (blockType == BlockType.Vacuum)
            {
                msgBuffer.Write((byte)BlockType.None);
            }
            else
            {
                msgBuffer.Write((byte)blockType);
            }
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
            
            //ConsoleWrite("BLOCKSET: " + x + " " + y + " " + z + " " + blockType.ToString());
        }

        public int newMap()
        {
            physicsEnabled = false;
            Thread.Sleep(2);
            // Create our block world, translating the coordinates out of the cave generator (where Z points down)
            BlockType[, ,] worldData = CaveGenerator.GenerateCaveSystem(MAPSIZE, includeLava, oreFactor, includeWater);
            blockList = new BlockType[MAPSIZE, MAPSIZE, MAPSIZE];
            blockListContent = new Int32[MAPSIZE, MAPSIZE, MAPSIZE,10];
            blockCreatorTeam = new PlayerTeam[MAPSIZE, MAPSIZE, MAPSIZE];
            for (ushort i = 0; i < MAPSIZE; i++)
                for (ushort j = 0; j < MAPSIZE; j++)
                    for (ushort k = 0; k < MAPSIZE; k++)
                    {
                        flowSleep[i, j, k] = false;
                        blockList[i, (ushort)(MAPSIZE - 1 - k), j] = worldData[i, j, k];

                        //if (blockList[i, (ushort)(MAPSIZE - 1 - k), j] == BlockType.Dirt)
                        //{
                        //    blockList[i, (ushort)(MAPSIZE - 1 - k), j] = BlockType.Sand;//covers map with block
                        //}
                        blockListContent[i,(ushort)(MAPSIZE - 1 - k), j, 0] = 0;//content data for blocks, such as pumps
                        blockListContent[i,(ushort)(MAPSIZE - 1 - k), j, 1] = 0;
                        blockListContent[i,(ushort)(MAPSIZE - 1 - k), j, 2] = 0;
                        blockListContent[i,(ushort)(MAPSIZE - 1 - k), j, 3] = 0;
                        blockCreatorTeam[i, j, k] = PlayerTeam.None;

                        if (i < 1 || j < 1 || k < 1 || i > MAPSIZE - 2 || j > MAPSIZE - 2 || k > MAPSIZE - 2)
                        {
                            blockList[i, (ushort)(MAPSIZE - 1 - k), j] = BlockType.None;
                        }
                    }

            for (int i = 0; i < MAPSIZE * 2; i++)
            {
                DoStuff();
            }

            physicsEnabled = true;
            return 1;
        }

        public double Get3DDistance(int x1, int y1, int z1, int x2, int y2, int z2)
        {
            int dx = x2 - x1;
            int dy = y2 - y1;
            int dz = z2 - z1;
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            return distance;
        }
        public double Distf(Vector3 x, Vector3 y)
        {
            float dx = y.X - x.X;
            float dy = y.Y - x.Y;
            float dz = y.Z - x.Z;
            float dist = (float)(Math.Sqrt(dx * dx + dy * dy + dz * dz));
            return dist;
        }
        public string GetExplosionPattern(int n)
        {
            string output="";
            int radius = (int)Math.Ceiling((double)varGetI("explosionradius"));
            int size = radius * 2 + 1;
            int center = radius; //Not adding one because arrays start from 0
            for (int z = n; z==n&&z<size; z++)
            {
                ConsoleWrite("Z" + z + ": ");
                output += "Z" + z + ": ";
                for (int x = 0; x < size; x++)
                {
                    string output1 = "";
                    for (int y = 0; y < size; y++)
                    {
                        output1+=tntExplosionPattern[x, y, z] ? "1, " : "0, ";
                    }
                    ConsoleWrite(output1);
                }
                output += "\n";
            }
            return "";
        }

        public void CalculateExplosionPattern()
        {
            int radius = (int)Math.Ceiling((double)varGetI("explosionradius"));
            int size = radius * 2 + 1;
            tntExplosionPattern = new bool[size, size, size];
            int center = radius; //Not adding one because arrays start from 0
            for(int x=0;x<size;x++)
                for(int y=0;y<size;y++)
                    for (int z = 0; z < size; z++)
                    {
                        if (x == y && y == z && z == center)
                            tntExplosionPattern[x, y, z] = true;
                        else
                        {
                            double distance = Get3DDistance(center, center, center, x, y, z);//Use center of blocks
                            if (distance <= (double)varGetI("explosionradius"))
                                tntExplosionPattern[x, y, z] = true;
                            else
                                tntExplosionPattern[x, y, z] = false;
                        }
                    }
        }

        public void status()
        {
            ConsoleWrite(varGetS("name"));//serverName);
            ConsoleWrite(playerList.Count + " / " + varGetI("maxplayers") + " players");
            foreach (string name in varBoolBindings.Keys)
            {
                ConsoleWrite(name + " = " + varBoolBindings[name]);
            }
        }

        public bool Start()
        {
            //Setup the variable toggles
            varBindingsInitialize();

            int tmpMaxPlayers = 16;

            // Read in from the config file.
            DatafileWriter dataFile = new DatafileWriter("server.config.txt");
            if (dataFile.Data.ContainsKey("winningcash"))
                winningCashAmount = uint.Parse(dataFile.Data["winningcash"], System.Globalization.CultureInfo.InvariantCulture);
            if (dataFile.Data.ContainsKey("includelava"))
                includeLava = bool.Parse(dataFile.Data["includelava"]);
            if (dataFile.Data.ContainsKey("includewater"))
                includeLava = bool.Parse(dataFile.Data["includewater"]);
            if (dataFile.Data.ContainsKey("orefactor"))
                oreFactor = uint.Parse(dataFile.Data["orefactor"], System.Globalization.CultureInfo.InvariantCulture);
            if (dataFile.Data.ContainsKey("maxplayers"))
                tmpMaxPlayers = (int)Math.Min(32, uint.Parse(dataFile.Data["maxplayers"], System.Globalization.CultureInfo.InvariantCulture));
            if (dataFile.Data.ContainsKey("public"))
                varSet("public", bool.Parse(dataFile.Data["public"]), true);
            if (dataFile.Data.ContainsKey("servername"))
                varSet("name", dataFile.Data["servername"], true);
            if (dataFile.Data.ContainsKey("sandbox"))
                varSet("sandbox", bool.Parse(dataFile.Data["sandbox"]), true);
            if (dataFile.Data.ContainsKey("notnt"))
                varSet("tnt", !bool.Parse(dataFile.Data["notnt"]), true);
            if (dataFile.Data.ContainsKey("sphericaltnt"))
                varSet("stnt", bool.Parse(dataFile.Data["sphericaltnt"]), true);
            if (dataFile.Data.ContainsKey("insane"))
                varSet("insane", bool.Parse(dataFile.Data["insane"]), true);
            if (dataFile.Data.ContainsKey("roadabsorbs"))
                varSet("roadabsorbs", bool.Parse(dataFile.Data["roadabsorbs"]), true);
            if (dataFile.Data.ContainsKey("minelava"))
                varSet("minelava", bool.Parse(dataFile.Data["minelava"]), true);
            if (dataFile.Data.ContainsKey("levelname"))
                levelToLoad = dataFile.Data["levelname"];
            if (dataFile.Data.ContainsKey("greeter"))
                varSet("greeter", dataFile.Data["greeter"],true);

            bool autoannounce = true;
            if (dataFile.Data.ContainsKey("autoannounce"))
                autoannounce = bool.Parse(dataFile.Data["autoannounce"]);

            // Load the ban-list.
            banList = LoadBanList();

            // Load the admin-list
            admins = LoadAdminList();

            if (tmpMaxPlayers>=0)
                varSet("maxplayers", tmpMaxPlayers, true);

            // Initialize the server.
            NetConfiguration netConfig = new NetConfiguration("InfiniminerPlus");
            netConfig.MaxConnections = (int)varGetI("maxplayers");
            netConfig.Port = 5565;
            netServer = new InfiniminerNetServer(netConfig);
            netServer.SetMessageTypeEnabled(NetMessageType.ConnectionApproval, true);
            //netServer.SimulatedMinimumLatency = 0.1f;
            //netServer.SimulatedLatencyVariance = 0.05f;
            //netServer.SimulatedLoss = 0.1f;
            //netServer.SimulatedDuplicates = 0.05f;
            netServer.Start();

            // Initialize variables we'll use.
            NetBuffer msgBuffer = netServer.CreateBuffer();
            NetMessageType msgType;
            NetConnection msgSender;

            // Store the last time that we did a flow calculation.
            DateTime lastFlowCalc = DateTime.Now;
            int secondflow = 0;
            //Check if we should autoload a level
            if (dataFile.Data.ContainsKey("autoload") && bool.Parse(dataFile.Data["autoload"]))
            {
                blockList = new BlockType[MAPSIZE, MAPSIZE, MAPSIZE];
                blockCreatorTeam = new PlayerTeam[MAPSIZE, MAPSIZE, MAPSIZE];
                LoadLevel(levelToLoad);

                lavaBlockCount = 0;
                waterBlockCount = 0;

                for (ushort i = 0; i < MAPSIZE; i++)
                    for (ushort j = 0; j < MAPSIZE; j++)
                        for (ushort k = 0; k < MAPSIZE; k++)
                        {
                            if (blockList[i, j, k] == BlockType.Lava)
                            {
                                lavaBlockCount += 1;
                            }
                            else if (blockList[i, j, k] == BlockType.Water)
                            {
                                waterBlockCount += 1;
                            }
                        }

                ConsoleWrite(waterBlockCount + " water blocks, " + lavaBlockCount + " lava blocks."); 
            }
            else
            {
                // Calculate initial lava flows.
                ConsoleWrite("CALCULATING INITIAL LIQUID BLOCKS");
                newMap();

                lavaBlockCount = 0;
                waterBlockCount = 0;

                for (ushort i = 0; i < MAPSIZE; i++)
                    for (ushort j = 0; j < MAPSIZE; j++)
                        for (ushort k = 0; k < MAPSIZE; k++)
                        {
                            if (blockList[i, j, k] == BlockType.Lava)
                            {
                                lavaBlockCount += 1;
                            }
                            else if (blockList[i, j, k] == BlockType.Water)
                            {
                                waterBlockCount += 1;
                            }
                        }

                ConsoleWrite(waterBlockCount + " water blocks, " + lavaBlockCount + " lava blocks.");           
            }
            

            //Caculate the shape of spherical tnt explosions
            CalculateExplosionPattern();

            // Send the initial server list update.
            if (autoannounce)
                PublicServerListUpdate(true);

            lastMapBackup = DateTime.Now;
           
            DateTime lastFPScheck = DateTime.Now;
            double frameRate = 0;
            Thread physics;
            // Main server loop!
            ConsoleWrite("SERVER READY");

            physics = new Thread(new ThreadStart(this.DoPhysics));
            physics.Priority = ThreadPriority.Normal;
            physics.Start();

            if (!physics.IsAlive)
            {
                ConsoleWrite("Physics thread is limp.");
            }

            while (keepRunning)
            {
                if (!physics.IsAlive)
                {
                    ConsoleWrite("Physics thread died.");
                   // physics.Abort();
                   // physics.Join();
                    //physics.Start();
                }

                frameCount = frameCount + 1;
                if (lastFPScheck <= DateTime.Now - TimeSpan.FromMilliseconds(1000))
                {
                    lastFPScheck = DateTime.Now;
                    frameRate = frameCount;// / gameTime.ElapsedTotalTime.TotalSeconds;
                    
                    if (sleeping == false && frameCount < 20)
                    {
                        ConsoleWrite("Heavy load: " + frameCount + " FPS");
                    }
                    frameCount = 0;
                }
                
                // Process any messages that are here.
                while (netServer.ReadMessage(msgBuffer, out msgType, out msgSender))
                {
                    try
                    {
                        switch (msgType)
                        {
                            case NetMessageType.ConnectionApproval:
                                {
                                    Player newPlayer = new Player(msgSender, null);
                                    newPlayer.Handle = Defines.Sanitize(msgBuffer.ReadString()).Trim();
                                    if (newPlayer.Handle.Length == 0)
                                    {
                                        newPlayer.Handle = "Player";
                                    }

                                    string clientVersion = msgBuffer.ReadString();
                                    if (clientVersion != Defines.INFINIMINER_VERSION)
                                    {
                                        msgSender.Disapprove("VER;" + Defines.INFINIMINER_VERSION);
                                    }
                                    else if (banList.Contains(newPlayer.IP))
                                    {
                                        msgSender.Disapprove("BAN;");
                                    }/*
                                else if (playerList.Count == maxPlayers)
                                {
                                    msgSender.Disapprove("FULL;");
                                }*/
                                    else
                                    {
                                        if (admins.ContainsKey(newPlayer.IP))
                                            newPlayer.admin = admins[newPlayer.IP];
                                        playerList[msgSender] = newPlayer;
                                        //Check if we should compress the map for the client
                                        try
                                        {
                                            bool compression = msgBuffer.ReadBoolean();
                                            if (compression)
                                                playerList[msgSender].compression = true;
                                        } catch { }
                                        toGreet.Add(msgSender);
                                        this.netServer.SanityCheck(msgSender);
                                        msgSender.Approve();
                                        PublicServerListUpdate(true);
                                    }
                                }
                                break;

                            case NetMessageType.StatusChanged:
                                {
                                    if (!this.playerList.ContainsKey(msgSender))
                                    {
                                        break;
                                    }

                                    Player player = playerList[msgSender];

                                    if (msgSender.Status == NetConnectionStatus.Connected)
                                    {
                                        if (sleeping == true)
                                        {
                                            sleeping = false;
                                            physicsEnabled = true;
                                        }
                                        ConsoleWrite("CONNECT: " + playerList[msgSender].Handle + " ( " + playerList[msgSender].IP + " )");
                                        SendCurrentMap(msgSender);
                                        SendPlayerJoined(player);
                                        PublicServerListUpdate();
                                    }

                                    else if (msgSender.Status == NetConnectionStatus.Disconnected)
                                    {
                                        ConsoleWrite("DISCONNECT: " + playerList[msgSender].Handle);
                                        SendPlayerLeft(player, player.Kicked ? "WAS KICKED FROM THE GAME!" : "HAS ABANDONED THEIR DUTIES!");
                                        if (playerList.ContainsKey(msgSender))
                                            playerList.Remove(msgSender);

                                        sleeping = true;
                                        foreach (Player p in playerList.Values)
                                        {
                                            sleeping = false;
                                        }

                                        if (sleeping == true)
                                        {
                                            ConsoleWrite("HIBERNATING");
                                            physicsEnabled = false;
                                        }

                                        PublicServerListUpdate();
                                    }
                                }
                                break;

                            case NetMessageType.Data:
                                {
                                    if (!this.playerList.ContainsKey(msgSender))
                                    {
                                        break;
                                    }

                                    Player player = playerList[msgSender];
                                    InfiniminerMessage dataType = (InfiniminerMessage)msgBuffer.ReadByte();
                                    switch (dataType)
                                    {
                                        case InfiniminerMessage.ChatMessage:
                                            {
                                                // Read the data from the packet.
                                                ChatMessageType chatType = (ChatMessageType)msgBuffer.ReadByte();
                                                string chatString = Defines.Sanitize(msgBuffer.ReadString());
                                                if (!ProcessCommand(chatString,GetAdmin(playerList[msgSender].IP),playerList[msgSender]))
                                                {
                                                    ConsoleWrite("CHAT: (" + player.Handle + ") " + chatString);

                                                    // Append identifier information.
                                                    if (chatType == ChatMessageType.SayAll)
                                                        chatString = player.Handle + " (ALL): " + chatString;
                                                    else
                                                        chatString = player.Handle + " (TEAM): " + chatString;

                                                    // Construct the message packet.
                                                    NetBuffer chatPacket = netServer.CreateBuffer();
                                                    chatPacket.Write((byte)InfiniminerMessage.ChatMessage);
                                                    chatPacket.Write((byte)((player.Team == PlayerTeam.Red) ? ChatMessageType.SayRedTeam : ChatMessageType.SayBlueTeam));
                                                    chatPacket.Write(chatString);

                                                    // Send the packet to people who should recieve it.
                                                    foreach (Player p in playerList.Values)
                                                    {
                                                        if (chatType == ChatMessageType.SayAll ||
                                                            chatType == ChatMessageType.SayBlueTeam && p.Team == PlayerTeam.Blue ||
                                                            chatType == ChatMessageType.SayRedTeam && p.Team == PlayerTeam.Red)
                                                            if (p.NetConn.Status == NetConnectionStatus.Connected)
                                                                netServer.SendMessage(chatPacket, p.NetConn, NetChannel.ReliableInOrder3);
                                                    }
                                                }
                                            }
                                            break;

                                        case InfiniminerMessage.UseTool:
                                            {
                                                Vector3 playerPosition = msgBuffer.ReadVector3();
                                                Vector3 playerHeading = msgBuffer.ReadVector3();
                                                PlayerTools playerTool = (PlayerTools)msgBuffer.ReadByte();
                                                BlockType blockType = (BlockType)msgBuffer.ReadByte();
                                                switch (playerTool)
                                                {
                                                    case PlayerTools.Pickaxe:
                                                        UsePickaxe(player, playerPosition, playerHeading);
                                                        break;
                                                    case PlayerTools.ConstructionGun:
                                                        UseConstructionGun(player, playerPosition, playerHeading, blockType);
                                                        break;
                                                    case PlayerTools.DeconstructionGun:
                                                        UseDeconstructionGun(player, playerPosition, playerHeading);
                                                        break;
                                                    case PlayerTools.ProspectingRadar:
                                                        UseSignPainter(player, playerPosition, playerHeading);
                                                        break;
                                                    case PlayerTools.Detonator:
                                                        UseDetonator(player);
                                                        break;
                                                    case PlayerTools.SpawnItem:
                                                        SpawnItem(player, playerPosition, playerHeading);
                                                        break;
                                                }
                                            }
                                            break;

                                        case InfiniminerMessage.SelectClass:
                                            {
                                                PlayerClass playerClass = (PlayerClass)msgBuffer.ReadByte();
                                                ConsoleWrite("SELECT_CLASS: " + player.Handle + ", " + playerClass.ToString());
                                                switch (playerClass)
                                                {
                                                    case PlayerClass.Engineer:
                                                        player.OreMax = 350;
                                                        player.WeightMax = 4;
                                                        player.HealthMax = 400;
                                                        player.Health = player.HealthMax;
                                                        break;
                                                    case PlayerClass.Miner:
                                                        player.OreMax = 200;
                                                        player.WeightMax = 8;
                                                        player.HealthMax = 400;
                                                        player.Health = player.HealthMax;
                                                        break;
                                                    case PlayerClass.Prospector:
                                                        player.OreMax = 200;
                                                        player.WeightMax = 4;
                                                        player.HealthMax = 400;
                                                        player.Health = player.HealthMax;
                                                        break;
                                                    case PlayerClass.Sapper:
                                                        player.OreMax = 200;
                                                        player.WeightMax = 4;
                                                        player.HealthMax = 400;
                                                        player.Health = player.HealthMax;
                                                        break;
                                                }
                                                SendResourceUpdate(player);
                                            }
                                            break;

                                        case InfiniminerMessage.PlayerSetTeam:
                                            {
                                                PlayerTeam playerTeam = (PlayerTeam)msgBuffer.ReadByte();
                                                ConsoleWrite("SELECT_TEAM: " + player.Handle + ", " + playerTeam.ToString());
                                                player.Team = playerTeam;
                                                SendResourceUpdate(player);
                                                SendPlayerSetTeam(player);
                                            }
                                            break;

                                        case InfiniminerMessage.PlayerDead:
                                            {
                                                ConsoleWrite("PLAYER_DEAD: " + player.Handle);
                                                player.Ore = 0;
                                                player.Cash = 0;
                                                player.Weight = 0;
                                                player.Health = 0;
                                                player.Alive = false;
                                                SendResourceUpdate(player);
                                                SendPlayerDead(player);

                                                string deathMessage = msgBuffer.ReadString();
                                                if (deathMessage != "")
                                                {
                                                    msgBuffer = netServer.CreateBuffer();
                                                    msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);
                                                    msgBuffer.Write((byte)(player.Team == PlayerTeam.Red ? ChatMessageType.SayRedTeam : ChatMessageType.SayBlueTeam));
                                                    msgBuffer.Write(player.Handle + " " + deathMessage);
                                                    foreach (NetConnection netConn in playerList.Keys)
                                                        if (netConn.Status == NetConnectionStatus.Connected)
                                                            netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder3);
                                                }
                                            }
                                            break;

                                        case InfiniminerMessage.PlayerAlive:
                                            {
                                                if (toGreet.Contains(msgSender))
                                                {
                                                    string greeting = varGetS("greeter");
                                                    greeting = greeting.Replace("[name]", playerList[msgSender].Handle);
                                                    if (greeting != "")
                                                    {
                                                        NetBuffer greetBuffer = netServer.CreateBuffer();
                                                        greetBuffer.Write((byte)InfiniminerMessage.ChatMessage);
                                                        greetBuffer.Write((byte)ChatMessageType.SayAll);
                                                        greetBuffer.Write(Defines.Sanitize(greeting));
                                                        netServer.SendMessage(greetBuffer, msgSender, NetChannel.ReliableInOrder3);
                                                    }
                                                    toGreet.Remove(msgSender);
                                                }
                                                ConsoleWrite("PLAYER_ALIVE: " + player.Handle);
                                                player.Ore = 0;
                                                player.Cash = 0;
                                                player.Weight = 0;
                                                player.Health = player.HealthMax;
                                                player.Alive = true;
                                                SendResourceUpdate(player);
                                                SendPlayerAlive(player);
                                            }
                                            break;

                                        case InfiniminerMessage.PlayerUpdate:
                                            {
                                                player.Position = Auth_Position(msgBuffer.ReadVector3(),player);
                                                player.Heading = Auth_Heading(msgBuffer.ReadVector3());
                                                player.Tool = (PlayerTools)msgBuffer.ReadByte();
                                                player.UsingTool = msgBuffer.ReadBoolean();
                                                SendPlayerUpdate(player);
                                            }
                                            break;
                                        case InfiniminerMessage.PlayerUpdate1://minus position
                                            {
                                                player.Heading = Auth_Heading(msgBuffer.ReadVector3());
                                                player.Tool = (PlayerTools)msgBuffer.ReadByte();
                                                player.UsingTool = msgBuffer.ReadBoolean();
                                                SendPlayerUpdate(player);
                                            }
                                            break;
                                        case InfiniminerMessage.PlayerUpdate2://minus position and heading
                                            {
                                                player.Tool = (PlayerTools)msgBuffer.ReadByte();
                                                player.UsingTool = msgBuffer.ReadBoolean();
                                                SendPlayerUpdate(player);
                                            }
                                            break;
                                        case InfiniminerMessage.PlayerHurt://client speaks of fall damage
                                            {
                                                uint newhp = msgBuffer.ReadUInt32();
                                                if (newhp < player.Health)
                                                {
                                                    player.Health = newhp;
                                                    if (player.Health < 1)
                                                    {
                                                        player.Ore = 0;//should be calling death function for player
                                                        player.Cash = 0;
                                                        player.Weight = 0;
                                                        player.Health = 0;
                                                        player.Alive = false;

                                                        SendResourceUpdate(player);
                                                        SendPlayerDead(player);
                                                    }
                                                }
                                            }
                                            break;
                                        case InfiniminerMessage.PlayerInteract://client speaks of mashing on block
                                            {
                                                player.Position = Auth_Position(msgBuffer.ReadVector3(), player);

                                                uint btn = msgBuffer.ReadUInt32();
                                                uint btnx = msgBuffer.ReadUInt32();
                                                uint btny = msgBuffer.ReadUInt32();
                                                uint btnz = msgBuffer.ReadUInt32();

                                                if (blockList[btnx, btny, btnz] == BlockType.Pump || blockList[btnx, btny, btnz] == BlockType.Pipe || blockList[btnx, btny, btnz] == BlockType.Generator)
                                                {
                                                    if (Get3DDistance((int)btnx, (int)btny, (int)btnz, (int)player.Position.X, (int)player.Position.Y, (int)player.Position.Z) < 4)
                                                    {
                                                        PlayerInteract(player,btn, btnx, btny, btnz);
                                                    }
                                                }
                                            }
                                            break;
                                        case InfiniminerMessage.DepositOre:
                                            {
                                                DepositOre(player);
                                                foreach (Player p in playerList.Values)
                                                    SendResourceUpdate(p);
                                            }
                                            break;

                                        case InfiniminerMessage.WithdrawOre:
                                            {
                                                WithdrawOre(player);
                                                foreach (Player p in playerList.Values)
                                                    SendResourceUpdate(p);
                                            }
                                            break;

                                        case InfiniminerMessage.PlayerPing:
                                            {
                                                SendPlayerPing((uint)msgBuffer.ReadInt32());
                                            }
                                            break;

                                        case InfiniminerMessage.PlaySound:
                                            {
                                                InfiniminerSound sound = (InfiniminerSound)msgBuffer.ReadByte();
                                                Vector3 position = msgBuffer.ReadVector3();
                                                PlaySound(sound, position);
                                            }
                                            break;

                                        case InfiniminerMessage.GetItem:
                                            {
                                                //verify players position before get
                                                player.Position = Auth_Position(msgBuffer.ReadVector3(), player);
                                                
                                                GetItem(player,msgBuffer.ReadString());
                                            }
                                            break;
                                    }
                                }
                                break;
                        }
                    }
                    catch { }
                }

                //Time to backup map?
                TimeSpan mapUpdateTimeSpan = DateTime.Now - lastMapBackup;
                if (mapUpdateTimeSpan.TotalMinutes > 5)
                {
                    lastMapBackup = DateTime.Now;
                    SaveLevel("autoBK.lvl");
                }

                // Time to send a new server update?
                PublicServerListUpdate(); //It checks for public server / time span

                //Time to terminate finished map sending threads?
                TerminateFinishedThreads();

                // Check for players who are in the zone to deposit.
                DepositForPlayers();

                // Is it time to do a lava calculation? If so, do it!
                TimeSpan timeSpan = DateTime.Now - lastFlowCalc;
                if (timeSpan.TotalMilliseconds > 250)//needs separate timer for each substance
                {
                    lastFlowCalc = DateTime.Now;

                    //secondflow += 1;

                    //if (secondflow > 2)//every 2nd flow, remove the vacuum that prevent re-spread
                    //{
                    //    EraseVacuum();
                    //    secondflow = 0;
                    //}

                    foreach (Player p in playerList.Values)//regeneration
                    {
                        if (p.Alive)
                            if (p.Health >= p.HealthMax)
                            {
                                p.Health = p.HealthMax;
                            }
                            else
                            {
                                p.Health = p.Health + 1;
                                SendResourceUpdate(p);
                            }
                    }

                    //physics = new Thread(new ThreadStart(this.DoStuff)); 
                    //DoStuff();

                }

                // Handle console keypresses.
                while (Console.KeyAvailable)
                {
                    ConsoleKeyInfo keyInfo = Console.ReadKey();
                    if (keyInfo.Key == ConsoleKey.Enter)
                        ConsoleProcessInput();
                    else if (keyInfo.Key == ConsoleKey.Backspace)
                    {
                        if (consoleInput.Length > 0)
                            consoleInput = consoleInput.Substring(0, consoleInput.Length - 1);
                        ConsoleRedraw();
                    }
                    else
                    {
                        consoleInput += keyInfo.KeyChar;
                        ConsoleRedraw();
                    }
                }

                // Is the game over?
                if (winningTeam != PlayerTeam.None && !restartTriggered)
                {
                    BroadcastGameOver();
                    restartTriggered = true;
                    restartTime = DateTime.Now.AddSeconds(10);
                }

                // Restart the server?
                if (restartTriggered && DateTime.Now > restartTime)
                {
                    SaveLevel("autosave_" + (UInt64)DateTime.Now.ToBinary() + ".lvl");

                    netServer.Shutdown("The server is restarting.");
                    return true;
                }

                // Pass control over to waiting threads.
                if(sleeping == true) {
                    Thread.Sleep(50);
                }
                else
                {
                  //  Thread.Sleep(0);
                }
            }

            MessageAll("Server going down NOW!");

            netServer.Shutdown("The server was terminated.");
            return false;
        }

        public void DepositForPlayers()
        {
            foreach (Player p in playerList.Values)
            {
                if (p.Position.Y > 64 - Defines.GROUND_LEVEL)
                    DepositCash(p);
            }

            if (varGetB("sandbox"))
                return;
            if (teamCashBlue >= winningCashAmount && winningTeam == PlayerTeam.None)
                winningTeam = PlayerTeam.Blue;
            if (teamCashRed >= winningCashAmount && winningTeam == PlayerTeam.None)
                winningTeam = PlayerTeam.Red;
        }

        public void EraseVacuum()
        {
            for (ushort i = 0; i < MAPSIZE; i++)
                for (ushort j = 0; j < MAPSIZE; j++)
                    for (ushort k = 0; k < MAPSIZE; k++)
                        if (blockList[i, j, k] == BlockType.Vacuum)
                        {
                            blockList[i, j, k] = BlockType.None;
                        }
        }
        public void DoPhysics()
        {
            DateTime lastFlowCalc = DateTime.Now;
            
            while (1==1)
            {
                while (physicsEnabled)
                {
                    TimeSpan timeSpan = DateTime.Now - lastFlowCalc;

                    if (timeSpan.TotalMilliseconds > 250)
                    {
                        lastFlowCalc = DateTime.Now;
                        DoStuff();

                    }
                    Thread.Sleep(2);
                }
                Thread.Sleep(50);
            }
        }
        public void DoStuff()
        {
            for (ushort i = 0; i < MAPSIZE; i++)
                for (ushort j = 0; j < MAPSIZE; j++)
                    for (ushort k = 0; k < MAPSIZE; k++)
                        if (blockList[i, j, k] == BlockType.Water && !flowSleep[i, j, k] || blockList[i, j, k] == BlockType.Lava && !flowSleep[i, j, k])//should be liquid check, not comparing each block
                        {//dowaterstuff //dolavastuff

                            BlockType liquid = blockList[i, j, k];
                            BlockType opposing = BlockType.None;

                            BlockType typeBelow = (j <= 0) ? BlockType.Vacuum : blockList[i, j - 1, k];//if j <= 0 then use block vacuum

                            if (blockList[i, j, k] == BlockType.Water)
                            {
                                opposing = BlockType.Lava;
                            }
                            else
                            {
                                //lava stuff
                                if (varGetB("roadabsorbs"))
                                {
                                    BlockType typeAbove = ((int)j == MAPSIZE - 1) ? BlockType.None : blockList[i, j + 1, k];
                                    if (typeAbove == BlockType.Road)
                                    {
                                        SetBlock(i, j, k, BlockType.Road, PlayerTeam.None);
                                    }
                                }
                            }

                            if (typeBelow != liquid && varGetB("insane"))
                            {
                                if (i > 0 && blockList[i - 1, j, k] == BlockType.None)
                                {
                                    SetBlock((ushort)(i - 1), j, k, liquid, PlayerTeam.None);
                                }
                                if (k > 0 && blockList[i, j, k - 1] == BlockType.None)
                                {
                                    SetBlock(i, j, (ushort)(k - 1), liquid, PlayerTeam.None);
                                }
                                if ((int)i < MAPSIZE - 1 && blockList[i + 1, j, k] == BlockType.None)
                                {
                                    SetBlock((ushort)(i + 1), j, k, liquid, PlayerTeam.None);
                                }
                                if ((int)k < MAPSIZE - 1 && blockList[i, j, k + 1] == BlockType.None)
                                {
                                    SetBlock(i, j, (ushort)(k + 1), liquid, PlayerTeam.None);
                                }
                            }

                            //check for conflicting lava//may need to check bounds
                            if (opposing != BlockType.None)
                            {
                                BlockType transform = BlockType.Rock;

                                if (i > 0 && blockList[i - 1, j, k] == opposing)
                                {
                                    SetBlock((ushort)(i - 1), j, k, transform, PlayerTeam.None);
                                }
                                if ((int)i < MAPSIZE - 1 && blockList[i + 1, j, k] == opposing)
                                {
                                    SetBlock((ushort)(i + 1), j, k, transform, PlayerTeam.None);
                                }
                                if (j > 0 && blockList[i, j - 1, k] == opposing)
                                {
                                    SetBlock(i, (ushort)(j - 1), k, transform, PlayerTeam.None);
                                }
                                if (j < MAPSIZE - 1 && blockList[i, (ushort)(j + 1), k] == opposing)
                                {
                                    SetBlock(i, (ushort)(j + 1), k, transform, PlayerTeam.None);
                                }
                                if (k > 0 && blockList[i, j, k - 1] == opposing)
                                {
                                    SetBlock(i, j, (ushort)(k - 1), transform, PlayerTeam.None);
                                }
                                if (k < MAPSIZE - 1 && blockList[i, j, k + 1] == opposing)
                                {
                                    SetBlock(i, j, (ushort)(k + 1), transform, PlayerTeam.None);
                                }

                                if (liquid == BlockType.Water)//make mud
                                {
                                    if (typeBelow == BlockType.Dirt)
                                    {
                                        
                                        SetBlock(i, (ushort)(j - 1), k, BlockType.Mud, PlayerTeam.None);
                                        blockListContent[i, j-1, k, 0] = 480;//two minutes @ 250ms 
                                    }
                                }
                            }
                            if (typeBelow != BlockType.None && typeBelow != liquid)//none//trying radius fill
                            {
                                for (ushort a = (ushort)(i - 1); a < i + 2; a++)
                                {
                                    for (ushort b = (ushort)(k - 1); b < k + 2; b++)
                                    {
                                        if (a == (ushort)(i - 1) && b == (ushort)(k - 1))
                                        {
                                            continue;
                                        }
                                        else if (a == i + 1 && b == k + 1)
                                        {
                                            continue;
                                        }
                                        else if (a == i - 1 && b == k + 1)
                                        {
                                            continue;
                                        }
                                        else if (a == i + 1 && b == (ushort)(k - 1))
                                        {
                                            continue;
                                        }

                                        if (blockList[i, j, k] != BlockType.None)//has our water block moved on us?
                                        {
                                            //water slides if standing on an edge
                                            if (a > 0 && b > 0 && a < 64 && b < 64 && j - 1 > 0)
                                                if (blockList[a, j - 1, b] == BlockType.None)
                                                {
                                                    SetBlock(a, (ushort)(j - 1), b, liquid, PlayerTeam.None);
                                                    SetBlock(i, j, k, BlockType.None, PlayerTeam.None);
                                                }
                                        }
                                    }
                                }
                            }
                            else if (typeBelow == liquid || typeBelow == BlockType.None)
                            {
                                ushort maxradius = 1;//1

                                while (maxradius < 25)//need to exclude old checks and require a* pathing check to source
                                {
                                    for (ushort a = (ushort)(-maxradius + i); a <= maxradius + i; a++)
                                    {
                                        for (ushort b = (ushort)(-maxradius + k); b <= maxradius + k; b++)
                                        {
                                            if (a > 0 && b > 0 && a < 64 && b < 64 && j - 1 > 0)
                                                if (blockList[a, j - 1, b] == BlockType.None)
                                                {
                                                    if (blockTrace(a, (ushort)(j - 1), b, i, (ushort)(j - 1), k, liquid))//needs to be a pathfind
                                                    {
                                                        SetBlock(a, (ushort)(j - 1), b, liquid, PlayerTeam.None);
                                                        SetBlock(i, j, k, BlockType.None, PlayerTeam.None);//using vacuum blocks temporary refill
                                                        maxradius = 30;
                                                        a = 65;
                                                        b = 65;
                                                    }
                                                }
                                        }

                                    }
                                    maxradius += 1;//prevent water spreading too large, this is mainly to stop loop size getting too large
                                }
                                if (maxradius != 30)//block could not find a new home
                                {
                                    flowSleep[i, j, k] = true;
                                    continue;//skip the surround check
                                }
                            }
                            //extra checks for sleep
                            uint surround = 0;
                            if (blockList[i, j, k] == liquid)
                            {
                                for (ushort a = (ushort)(-1 + i); a <= 1 + i; a++)
                                {
                                    for (ushort b = (ushort)(-1 + j); b <= 1 + j; b++)
                                    {
                                        for (ushort c = (ushort)(-1 + k); c <= 1 + k; c++)
                                        {
                                            if (a > 0 && b > 0 && c > 0 && a < 64 && b < 64 && c < 64)
                                            {
                                                if (blockList[a, b, c] != BlockType.None)
                                                {
                                                    surround += 1;//block is surrounded by types it cant move through
                                                }
                                            }
                                            else//surrounded by edge of map
                                            {
                                                surround += 1;
                                            }
                                        }
                                    }
                                }
                                if (surround >= 27)
                                {
                                    flowSleep[i, j, k] = true;
                                }
                            }
                        }
                        else if (blockList[i, j, k] == BlockType.Pump && blockListContent[i, j, k, 0] > 0)// content0 = determines if on
                        {//dopumpstuff
                            BlockType pumpheld = BlockType.None;

                            if (i + blockListContent[i, j, k, 2] < MAPSIZE && j + blockListContent[i, j, k, 3] < MAPSIZE && k + blockListContent[i, j, k, 4] < MAPSIZE && i + blockListContent[i, j, k, 2] > 0 && j + blockListContent[i, j, k, 3] > 0 && k + blockListContent[i, j, k, 4] > 0)
                            {
                                if (blockList[i + blockListContent[i, j, k, 2], j + blockListContent[i, j, k, 3], k + blockListContent[i, j, k, 4]] == BlockType.Water)
                                {
                                    pumpheld = BlockType.Water;
                                    SetBlock((ushort)(i + blockListContent[i, j, k, 2]), (ushort)(j + blockListContent[i, j, k, 3]), (ushort)(k + blockListContent[i, j, k, 4]), BlockType.None, PlayerTeam.None);
                                }
                                if (blockList[i + blockListContent[i, j, k, 2], j + blockListContent[i, j, k, 3], k + blockListContent[i, j, k, 4]] == BlockType.Lava)
                                {
                                    pumpheld = BlockType.Lava;
                                    SetBlock((ushort)(i + blockListContent[i, j, k, 2]), (ushort)(j + blockListContent[i, j, k, 3]), (ushort)(k + blockListContent[i, j, k, 4]), BlockType.None, PlayerTeam.None);
                                }

                                if (pumpheld != BlockType.None)
                                {
                                    if (i + blockListContent[i, j, k, 5] < MAPSIZE && j + blockListContent[i, j, k, 6] < MAPSIZE && k + blockListContent[i, j, k, 7] < MAPSIZE && i + blockListContent[i, j, k, 5] > 0 && j + blockListContent[i, j, k, 6] > 0 && k + blockListContent[i, j, k, 7] > 0)
                                    {
                                        if (blockList[i + blockListContent[i, j, k, 5], j + blockListContent[i, j, k, 6], k + blockListContent[i, j, k, 7]] == BlockType.None)
                                        {//check bounds
                                            SetBlock((ushort)(i + blockListContent[i, j, k, 5]), (ushort)(j + blockListContent[i, j, k, 6]), (ushort)(k + blockListContent[i, j, k, 7]), pumpheld, PlayerTeam.None);//places its contents in desired direction
                                        }
                                        else if (blockList[i + blockListContent[i, j, k, 5], j + blockListContent[i, j, k, 6], k + blockListContent[i, j, k, 7]] == pumpheld)//exit must be clear or same substance
                                        {
                                            for (ushort m = 2; m < 10; m++)//multiply exit area to fake upward/sideward motion
                                            {
                                                if (i + blockListContent[i, j, k, 5] * m < MAPSIZE && j + blockListContent[i, j, k, 6] * m < MAPSIZE && k + blockListContent[i, j, k, 7] * m < MAPSIZE && i + blockListContent[i, j, k, 5] * m > 0 && j + blockListContent[i, j, k, 6] * m > 0 && k + blockListContent[i, j, k, 7] * m > 0)
                                                {
                                                    if (blockList[i + blockListContent[i, j, k, 5] * m, j + blockListContent[i, j, k, 6] * m, k + blockListContent[i, j, k, 7] * m] == BlockType.None)
                                                    {
                                                        SetBlock((ushort)(i + blockListContent[i, j, k, 5] * m), (ushort)(j + blockListContent[i, j, k, 6] * m), (ushort)(k + blockListContent[i, j, k, 7] * m), pumpheld, PlayerTeam.None);//places its contents in desired direction at a distance
                                                        break;//done with this pump
                                                    }
                                                    else// if (blockList[i + blockListContent[i, j, k, 5] * m, j + blockListContent[i, j, k, 6] * m, k + blockListContent[i, j, k, 7] * m] != pumpheld)//check that we're not going through walls to pump this
                                                    {
                                                        break;//pipe has run aground .. and dont refund the intake
                                                    }
                                                }

                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else if (blockList[i, j, k] == BlockType.Spring)
                        {//dospringstuff
                            if (blockList[i, j - 1, k] == BlockType.None)
                            {
                                SetBlock(i, (ushort)(j - 1), k, BlockType.Water, PlayerTeam.None);
                            }
                        }
                        else if (blockList[i, j, k] == BlockType.Mud)//mud dries out
                        {
                            if (blockList[i, j-1, k] != BlockType.Water)
                            if (blockListContent[i, j, k, 0] < 1)
                            {
                                blockListContent[i, j, k, 0] = 0;
                                SetBlock(i, j, k, BlockType.Dirt, PlayerTeam.None);
                            }
                            else
                            {
                                blockListContent[i, j, k, 0] -= 1;
                            }
                        }
                        else if (blockList[i, j, k] == BlockType.Sand)//sand falls straight down and moves over edges
                        {
                            if (j - 1 > 0)
                            {
                                if (blockList[i, j - 1, k] == BlockType.None)
                                {
                                    SetBlock(i, (ushort)(j - 1), k, BlockType.Sand, PlayerTeam.None);
                                    SetBlock(i, j, k, BlockType.None, PlayerTeam.None);
                                    continue;
                                }
                                for (ushort m = 1; m < 2; m++)//how many squares to fall over
                                {
                                    if (i + m < MAPSIZE)
                                        if (blockList[i + m, j - 1, k] == BlockType.None)
                                        {
                                            SetBlock((ushort)(i + m), (ushort)(j - 1), k, BlockType.Sand, PlayerTeam.None);
                                            SetBlock(i, j, k, BlockType.None, PlayerTeam.None);
                                            continue;
                                        }
                                    if (i - m > 0)
                                        if (blockList[i - m, j - 1, k] == BlockType.None)
                                        {
                                            SetBlock((ushort)(i - m), (ushort)(j - 1), k, BlockType.Sand, PlayerTeam.None);
                                            SetBlock(i, j, k, BlockType.None, PlayerTeam.None);
                                            continue;
                                        }
                                    if (k + m < MAPSIZE)
                                        if (blockList[i, j - 1, k + m] == BlockType.None)
                                        {
                                            SetBlock(i, (ushort)(j - 1), (ushort)(k + m), BlockType.Sand, PlayerTeam.None);
                                            SetBlock(i, j, k, BlockType.None, PlayerTeam.None);
                                            continue;
                                        }
                                    if (k - m > 0)
                                        if (blockList[i, j - 1, k - m] == BlockType.None)
                                        {
                                            SetBlock(i, (ushort)(j - 1), (ushort)(k - m), BlockType.Sand, PlayerTeam.None);
                                            SetBlock(i, j, k, BlockType.None, PlayerTeam.None);
                                            continue;
                                        }
                                }
                            }
                        }
                        else if (blockList[i, j, k] == BlockType.Dirt)//loose dirt falls straight down
                        {
                            if (j + 1 < MAPSIZE && j - 1 > 0 && i - 1 > 0 && i + 1 < MAPSIZE && k - 1 > 0 && k + 1 < MAPSIZE)
                                if (blockList[i, j - 1, k] == BlockType.None)
                                if (blockList[i, j + 1, k] == BlockType.None && blockList[i + 1, j, k] == BlockType.None && blockList[i - 1, j, k] == BlockType.None && blockList[i, j, k+1] == BlockType.None && blockList[i, j, k-1] == BlockType.None)
                                {//no block above or below, so fall
                                    SetBlock(i, (ushort)(j - 1), k, BlockType.Dirt, PlayerTeam.None);
                                    SetBlock(i, j, k, BlockType.None, PlayerTeam.None);
                                    continue;
                                }   
                        }
                 
        }
        public void Disturb(ushort i, ushort j, ushort k)
        {
            for (ushort a = (ushort)(i-1); a <= 1 + i; a++)
                for (ushort b = (ushort)(j-1); b <= 1 + j; b++)
                    for (ushort c = (ushort)(k-1); c <= 1 + k; c++)
                        if (a > 0 && b > 0 && c > 0 && a < 64 && b < 64 && c < 64)
                        {
                            flowSleep[a, b, c] = false;
                        }
        }
        public BlockType BlockAtPoint(Vector3 point)
        {
            ushort x = (ushort)point.X;
            ushort y = (ushort)point.Y;
            ushort z = (ushort)point.Z;
            if (x <= 0 || y <= 0 || z <= 0 || (int)x > MAPSIZE - 1 || (int)y > MAPSIZE - 1 || (int)z > MAPSIZE - 1)
                return BlockType.None;
            return blockList[x, y, z];
        }

        public bool blockTrace(ushort oX,ushort oY,ushort oZ,ushort dX,ushort dY,ushort dZ,BlockType allow)//only traces x/y not depth
        {
            while (oX != dX || oY != dY || oZ != dZ)
            {
                if (oX - dX > 0)
                {
                    oX = (ushort)(oX - 1);
                    if (blockList[oX, oY, oZ] != allow)
                    {
                        return false;
                    }
                }
                else if (oX - dX < 0)
                {
                    oX = (ushort)(oX + 1);
                    if (blockList[oX, oY, oZ] != allow)
                    {
                        return false;
                    }
                }

                if (oZ - dZ > 0)
                {
                    oZ = (ushort)(oZ - 1);
                    if (blockList[oX, oY, oZ] != allow)
                    {
                        return false;
                    }
                }
                else if (oZ - dZ < 0)
                {
                    oZ = (ushort)(oZ + 1);
                    if (blockList[oX, oY, oZ] != allow)
                    {
                        return false;
                    }
                }
            }
            return true;
        }
        public bool RayCollision(Vector3 startPosition, Vector3 rayDirection, float distance, int searchGranularity, ref Vector3 hitPoint, ref Vector3 buildPoint)
        {
            Vector3 testPos = startPosition;
            Vector3 buildPos = startPosition;
            for (int i = 0; i < searchGranularity; i++)
            {
                testPos += rayDirection * distance / searchGranularity;
                BlockType testBlock = BlockAtPoint(testPos);
                if (testBlock != BlockType.None)
                {
                    hitPoint = testPos;
                    buildPoint = buildPos;
                    return true;
                }
                buildPos = testPos;
            }
            return false;
        }

        public Vector3 RayCollisionExact(Vector3 startPosition, Vector3 rayDirection, float distance, int searchGranularity, ref Vector3 hitPoint, ref Vector3 buildPoint)
        {
            Vector3 testPos = startPosition;
            Vector3 buildPos = startPosition;
           
            for (int i = 0; i < searchGranularity; i++)
            {
                testPos += rayDirection * distance / searchGranularity;
                BlockType testBlock = BlockAtPoint(testPos);
                if (testBlock != BlockType.None)
                {
                    hitPoint = testPos;
                    buildPoint = buildPos;
                    return hitPoint;
                }
                buildPos = testPos;
            }

            return startPosition;
        }

        public void UsePickaxe(Player player, Vector3 playerPosition, Vector3 playerHeading)
        {
            player.QueueAnimationBreak = true;
            
            // Figure out what we're hitting.
            Vector3 hitPoint = Vector3.Zero;
            Vector3 buildPoint = Vector3.Zero;
            if (!RayCollision(playerPosition, playerHeading, 2, 10, ref hitPoint, ref buildPoint))
                return;
            ushort x = (ushort)hitPoint.X;
            ushort y = (ushort)hitPoint.Y;
            ushort z = (ushort)hitPoint.Z;

            // Figure out what the result is.
            bool removeBlock = false;
            uint giveOre = 0;
            uint giveCash = 0;
            uint giveWeight = 0;

            InfiniminerSound sound = InfiniminerSound.DigDirt;

            switch (BlockAtPoint(hitPoint))
            {
                case BlockType.Lava:
                    if (varGetB("minelava"))
                    {
                        removeBlock = true;
                        sound = InfiniminerSound.DigDirt;
                    }
                    break;
                case BlockType.Water:
                    if (varGetB("minelava"))
                    {
                        removeBlock = true;
                        sound = InfiniminerSound.DigDirt;
                    }
                    break;
                case BlockType.Dirt:
                case BlockType.Mud:
                case BlockType.Sand:
                case BlockType.DirtSign:
                    removeBlock = true;
                    sound = InfiniminerSound.DigDirt;
                    break;

                case BlockType.Ore:
                    removeBlock = true;
                    giveOre = 20;
                    sound = InfiniminerSound.DigMetal;
                    break;

                case BlockType.Gold:
                    removeBlock = true;
                    giveWeight = 1;
                    giveCash = 100;
                    sound = InfiniminerSound.DigMetal;
                    break;

                case BlockType.Diamond:
                    removeBlock = true;
                    giveWeight = 1;
                    giveCash = 1000;
                    sound = InfiniminerSound.DigMetal;
                    break;
            }

            if (giveOre > 0)
            {
                if (player.Ore < player.OreMax)
                {
                    player.Ore = Math.Min(player.Ore + giveOre, player.OreMax);
                    SendResourceUpdate(player);
                }
            }

            if (giveWeight > 0)
            {
                if (player.Weight < player.WeightMax)
                {
                    player.Weight = Math.Min(player.Weight + giveWeight, player.WeightMax);
                    player.Cash += giveCash;
                    SendResourceUpdate(player);
                }
                else
                    removeBlock = false;
            }

            if (removeBlock)
            {
                SetBlock(x, y, z, BlockType.None, PlayerTeam.None);

                PlaySound(sound, player.Position);
            }
        }

        //private bool LocationNearBase(ushort x, ushort y, ushort z)
        //{
        //    for (int i=0; i<MAPSIZE; i++)
        //        for (int j=0; j<MAPSIZE; j++)
        //            for (int k = 0; k < MAPSIZE; k++)
        //                if (blockList[i, j, k] == BlockType.HomeBlue || blockList[i, j, k] == BlockType.HomeRed)
        //                {
        //                    double dist = Math.Sqrt(Math.Pow(x - i, 2) + Math.Pow(y - j, 2) + Math.Pow(z - k, 2));
        //                    if (dist < 3)
        //                        return true;
        //                }
        //    return false;
        //}
        public void SpawnItem(Player player, Vector3 playerPosition, Vector3 playerHeading)
        {
            bool actionFailed = false;

            // If there's no surface within range, bail.
            Vector3 hitPoint = Vector3.Zero;
            Vector3 buildPoint = Vector3.Zero;
            Vector3 exactPoint = Vector3.Zero;
            if (!RayCollision(playerPosition, playerHeading, 6, 25, ref hitPoint, ref buildPoint))
            {
                actionFailed = true;
            }
            else
            {
                exactPoint = RayCollisionExact(playerPosition, playerHeading, 6, 25, ref hitPoint, ref buildPoint);
            }
            ushort x = (ushort)buildPoint.X;
            ushort y = (ushort)buildPoint.Y;
            ushort z = (ushort)buildPoint.Z;

            if (x <= 0 || y <= 0 || z <= 0 || (int)x > MAPSIZE - 1 || (int)y > MAPSIZE - 1 || (int)z > MAPSIZE - 1)
                actionFailed = true;

            if (blockList[(ushort)hitPoint.X, (ushort)hitPoint.Y, (ushort)hitPoint.Z] == BlockType.Lava || blockList[(ushort)hitPoint.X, (ushort)hitPoint.Y, (ushort)hitPoint.Z] == BlockType.Water)
                actionFailed = true;

            if (actionFailed)
            {
                // Decharge the player's gun.
            //    TriggerConstructionGunAnimation(player, -0.2f);
            }
            else
            {
                // Fire the player's gun.
            //    TriggerConstructionGunAnimation(player, 0.5f);

                // Build the block.
                //hitPoint = RayCollision(playerPosition, playerHeading, 6, 25, ref hitPoint, ref buildPoint, 1);

                exactPoint.Y = exactPoint.Y + (float)1.0;//0.25 = items height

                SetItem(exactPoint, playerHeading, player.Team);
               // player.Ore -= blockCost;
               // SendResourceUpdate(player);

                // Play the sound.
                PlaySound(InfiniminerSound.ConstructionGun, player.Position);
            }            


        }
        public void UseConstructionGun(Player player, Vector3 playerPosition, Vector3 playerHeading, BlockType blockType)
        {
            bool actionFailed = false;

            // If there's no surface within range, bail.
            Vector3 hitPoint = Vector3.Zero;
            Vector3 buildPoint = Vector3.Zero;
            if (!RayCollision(playerPosition, playerHeading, 6, 25, ref hitPoint, ref buildPoint))
                actionFailed = true;

            // If the block is too expensive, bail.
            uint blockCost = BlockInformation.GetCost(blockType);
            if (varGetB("sandbox") && blockCost <= player.OreMax)
                blockCost = 0;
            if (blockCost > player.Ore)
                actionFailed = true;

            // If there's someone there currently, bail.
            ushort x = (ushort)buildPoint.X;
            ushort y = (ushort)buildPoint.Y;
            ushort z = (ushort)buildPoint.Z;
            foreach (Player p in playerList.Values)
            {
                if ((int)p.Position.X == x && (int)p.Position.Z == z && ((int)p.Position.Y == y || (int)p.Position.Y - 1 == y))
                    actionFailed = true;
            }

            // If it's out of bounds, bail.
            if (x <= 0 || y <= 0 || z <= 0 || (int)x > MAPSIZE - 1 || (int)y >= MAPSIZE - 1 || (int)z > MAPSIZE - 1)//y >= prevent blocks going too high on server
                actionFailed = true;

            // If it's near a base, bail.
            //if (LocationNearBase(x, y, z))
            //    actionFailed = true;

            // If it's lava, don't let them build off of lava.
            if (blockList[(ushort)hitPoint.X, (ushort)hitPoint.Y, (ushort)hitPoint.Z] == BlockType.Lava || blockList[(ushort)hitPoint.X, (ushort)hitPoint.Y, (ushort)hitPoint.Z] == BlockType.Water)
                actionFailed = true;

            if (actionFailed)
            {
                // Decharge the player's gun.
                TriggerConstructionGunAnimation(player, -0.2f);
            }
            else
            {
                // Fire the player's gun.
                TriggerConstructionGunAnimation(player, 0.5f);

                // Build the block.
                SetBlock(x, y, z, blockType, player.Team);
                player.Ore -= blockCost;
                SendResourceUpdate(player);

                // Play the sound.
                PlaySound(InfiniminerSound.ConstructionGun, player.Position);

                // If it's an explosive block, add it to our list.
                if (blockType == BlockType.Explosive)
                    player.ExplosiveList.Add(buildPoint);
            }            
        }

        public void UseDeconstructionGun(Player player, Vector3 playerPosition, Vector3 playerHeading)
        {
            bool actionFailed = false;

            // If there's no surface within range, bail.
            Vector3 hitPoint = Vector3.Zero;
            Vector3 buildPoint = Vector3.Zero;
            if (!RayCollision(playerPosition, playerHeading, 6, 25, ref hitPoint, ref buildPoint))
                actionFailed = true;
            ushort x = (ushort)hitPoint.X;
            ushort y = (ushort)hitPoint.Y;
            ushort z = (ushort)hitPoint.Z;

            // If this is another team's block, bail.
            if (blockCreatorTeam[x, y, z] != player.Team)
                actionFailed = true;

            BlockType blockType = blockList[x, y, z];
            if (!(blockType == BlockType.SolidBlue ||
                blockType == BlockType.SolidRed ||
                blockType == BlockType.BankBlue ||
                blockType == BlockType.BankRed ||
                blockType == BlockType.Jump ||
                blockType == BlockType.Ladder ||
                blockType == BlockType.Road ||
                blockType == BlockType.Shock ||
                blockType == BlockType.BeaconRed ||
                blockType == BlockType.BeaconBlue ||
                blockType == BlockType.Water ||
                blockType == BlockType.TransBlue ||
                blockType == BlockType.TransRed ||
                blockType == BlockType.Generator ||
                blockType == BlockType.Pipe ||
                blockType == BlockType.Pump ||
                blockType == BlockType.Controller ||
                blockType == BlockType.Water
                ))
                actionFailed = true;

            if (actionFailed)
            {
                // Decharge the player's gun.
                TriggerConstructionGunAnimation(player, -0.2f);
            }
            else
            {
                // Fire the player's gun.
                TriggerConstructionGunAnimation(player, 0.5f);

                // Remove the block.
                SetBlock(x, y, z, BlockType.None, PlayerTeam.None);
                PlaySound(InfiniminerSound.ConstructionGun, player.Position);
            }
        }

        public void TriggerConstructionGunAnimation(Player player, float animationValue)
        {
            if (player.NetConn.Status != NetConnectionStatus.Connected)
                return;

            // ore, cash, weight, max ore, max weight, team ore, red cash, blue cash, all uint
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.TriggerConstructionGunAnimation);
            msgBuffer.Write(animationValue);
            netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder1);
        }

        public void UseSignPainter(Player player, Vector3 playerPosition, Vector3 playerHeading)
        {
            // If there's no surface within range, bail.
            Vector3 hitPoint = Vector3.Zero;
            Vector3 buildPoint = Vector3.Zero;
            if (!RayCollision(playerPosition, playerHeading, 4, 25, ref hitPoint, ref buildPoint))
                return;
            ushort x = (ushort)hitPoint.X;
            ushort y = (ushort)hitPoint.Y;
            ushort z = (ushort)hitPoint.Z;

            if (blockList[x, y, z] == BlockType.Dirt)
            {
                SetBlock(x, y, z, BlockType.DirtSign, PlayerTeam.None);
                PlaySound(InfiniminerSound.ConstructionGun, player.Position);
            }
            else if (blockList[x, y, z] == BlockType.DirtSign)
            {
                SetBlock(x, y, z, BlockType.Dirt, PlayerTeam.None);
                PlaySound(InfiniminerSound.ConstructionGun, player.Position);
            }
        }

        public void ExplosionEffectAtPoint(int x, int y, int z)
        {
            // Send off the explosion to clients.
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.TriggerExplosion);
            msgBuffer.Write(new Vector3(x, y, z));
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
            //Or not, there's no dedicated function for this effect >:(
        }

        public void DetonateAtPoint(int x, int y, int z)
        {
            // Remove the block that is detonating.
            SetBlock((ushort)(x), (ushort)(y), (ushort)(z), BlockType.None, PlayerTeam.None);

            // Remove this from any explosive lists it may be in.
            foreach (Player p in playerList.Values)
                p.ExplosiveList.Remove(new Vector3(x, y, z));

            // Detonate the block.
            if (!varGetB("stnt"))
            {
                for (int dx = -2; dx <= 2; dx++)
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dz = -2; dz <= 2; dz++)
                        {
                            // Check that this is a sane block position.
                            if (x + dx <= 0 || y + dy <= 0 || z + dz <= 0 || x + dx > MAPSIZE - 1 || y + dy > MAPSIZE - 1 || z + dz > MAPSIZE - 1)
                                continue;

                            // Chain reactions!
                            if (blockList[x + dx, y + dy, z + dz] == BlockType.Explosive)
                                DetonateAtPoint(x + dx, y + dy, z + dz);

                            // Detonation of normal blocks.
                            bool destroyBlock = false;
                            switch (blockList[x + dx, y + dy, z + dz])
                            {
                                case BlockType.Rock:
                                case BlockType.Dirt:
                                case BlockType.Mud:
                                case BlockType.Sand:
                                case BlockType.DirtSign:
                                case BlockType.Ore:
                                case BlockType.SolidRed:
                                case BlockType.SolidBlue:
                                case BlockType.TransRed:
                                case BlockType.TransBlue:
                                case BlockType.Water:
                                case BlockType.Ladder:
                                case BlockType.Shock:
                                case BlockType.Jump:
                                case BlockType.Explosive:
                                case BlockType.Lava:
                                case BlockType.Spring:
                                case BlockType.Road:
                                    destroyBlock = true;
                                    break;
                            }
                            if (destroyBlock)
                                SetBlock((ushort)(x + dx), (ushort)(y + dy), (ushort)(z + dz), BlockType.None, PlayerTeam.None);
                        }
            }
            else
            {
                int radius = (int)Math.Ceiling((double)varGetI("explosionradius"));
                int size = radius * 2 + 1;
                int center = radius+1;
                //ConsoleWrite("Radius: " + radius + ", Size: " + size + ", Center: " + center);
                for (int dx = -center+1; dx < center; dx++)
                    for (int dy = -center+1; dy < center; dy++)
                        for (int dz = -center+1; dz < center; dz++)
                        {
                            if (tntExplosionPattern[dx+center-1, dy+center-1, dz+center-1]) //Warning, code duplication ahead!
                            {
                                // Check that this is a sane block position.
                                if (x + dx <= 0 || y + dy <= 0 || z + dz <= 0 || x + dx > MAPSIZE - 1 || y + dy > MAPSIZE - 1 || z + dz > MAPSIZE - 1)
                                    continue;

                                // Chain reactions!
                                if (blockList[x + dx, y + dy, z + dz] == BlockType.Explosive)
                                    DetonateAtPoint(x + dx, y + dy, z + dz);

                                // Detonation of normal blocks.
                                bool destroyBlock = false;
                                switch (blockList[x + dx, y + dy, z + dz])
                                {
                                    case BlockType.Rock:
                                    case BlockType.Dirt:
                                    case BlockType.Mud:
                                    case BlockType.Sand:
                                    case BlockType.DirtSign:
                                    case BlockType.Ore:
                                    case BlockType.SolidRed:
                                    case BlockType.SolidBlue:
                                    case BlockType.TransRed:
                                    case BlockType.TransBlue:
                                    case BlockType.Water:
                                    case BlockType.Ladder:
                                    case BlockType.Shock:
                                    case BlockType.Jump:
                                    case BlockType.Explosive:
                                    case BlockType.Lava:
                                    case BlockType.Spring:
                                    case BlockType.Road:
                                        destroyBlock = true;
                                        break;
                                }
                                if (destroyBlock)
                                    SetBlock((ushort)(x + dx), (ushort)(y + dy), (ushort)(z + dz), BlockType.None, PlayerTeam.None);
                            }
                        }
            }
            ExplosionEffectAtPoint(x, y, z);
        }

        public void UseDetonator(Player player)
        {
            while (player.ExplosiveList.Count > 0)
            {
                Vector3 blockPos = player.ExplosiveList[0];
                ushort x = (ushort)blockPos.X;
                ushort y = (ushort)blockPos.Y;
                ushort z = (ushort)blockPos.Z;

                if (blockList[x, y, z] != BlockType.Explosive)
                    player.ExplosiveList.RemoveAt(0);
                else if (!varGetB("tnt"))
                {
                    player.ExplosiveList.RemoveAt(0);
                    ExplosionEffectAtPoint(x,y,z);
                    // Remove the block that is detonating.
                    SetBlock(x, y, z, BlockType.None, PlayerTeam.None);
                }
                else
                    DetonateAtPoint(x, y, z);
            }
        }

        public void PlayerInteract(Player player, uint btn, uint x, uint y, uint z)
        {
            if (blockList[x, y, z] == BlockType.Pipe)
            {
                ConsoleWrite("clicked "+btn+" on pipe!");
            }
            else if (blockList[x, y, z] == BlockType.Pump)
            {
                if (btn == 1)
                {
                    if (blockListContent[x, y, z, 0] == 0)
                    {
                        ConsoleWrite("Pump on!");
                        blockListContent[x, y, z, 0] = 1;
                    }
                    else
                    {
                        ConsoleWrite("Pump off!");
                        blockListContent[x, y, z, 0] = 0;
                    }
                }
                else if (btn == 2)
                {
                    if (blockListContent[x, y, z, 1] < 5)//rotate
                    {
                        blockListContent[x, y, z, 1] += 1;

                        ConsoleWrite("Rotated to:" + blockListContent[x, y, z, 1]);

                        if (blockListContent[x, y, z, 1] == 1)
                        {
                            blockListContent[x, y, z, 2] = 0;//x input
                            blockListContent[x, y, z, 3] = -1;//y input
                            blockListContent[x, y, z, 4] = 0;//z input
                            blockListContent[x, y, z, 5] = 1;//x output
                            blockListContent[x, y, z, 6] = 0;//y output
                            blockListContent[x, y, z, 7] = 0;//z output
                            //pulls from below, pumps to side
                        }
                        else if (blockListContent[x, y, z, 1] == 2)
                        {
                            blockListContent[x, y, z, 2] = 0;//x input
                            blockListContent[x, y, z, 3] = -1;//y input
                            blockListContent[x, y, z, 4] = 0;//z input
                            blockListContent[x, y, z, 5] = -1;//x output
                            blockListContent[x, y, z, 6] = 0;//y output
                            blockListContent[x, y, z, 7] = 0;//z output
                            //pulls from below, pumps to otherside
                        }
                        else if (blockListContent[x, y, z, 1] == 3)
                        {
                            blockListContent[x, y, z, 2] = 0;//x input
                            blockListContent[x, y, z, 3] = -1;//y input
                            blockListContent[x, y, z, 4] = 0;//z input
                            blockListContent[x, y, z, 5] = 0;//x output
                            blockListContent[x, y, z, 6] = 0;//y output
                            blockListContent[x, y, z, 7] = 1;//z output
                            //pulls from below, pumps to otherside
                        }
                        else if (blockListContent[x, y, z, 1] == 4)
                        {
                            blockListContent[x, y, z, 2] = 0;//x input
                            blockListContent[x, y, z, 3] = -1;//y input
                            blockListContent[x, y, z, 4] = 0;//z input
                            blockListContent[x, y, z, 5] = 0;//x output
                            blockListContent[x, y, z, 6] = 0;//y output
                            blockListContent[x, y, z, 7] = -1;//z output
                            //pulls from below, pumps to otherside
                        }
                    }
                    else
                    {
                        blockListContent[x, y, z, 1] = 0;//reset rotation
                        ConsoleWrite("Rotated to:" + blockListContent[x, y, z, 1]);
                        
                        blockListContent[x, y, z, 2] = 0;//x input
                        blockListContent[x, y, z, 3] = -1;//y input
                        blockListContent[x, y, z, 4] = 0;//z input
                        blockListContent[x, y, z, 5] = 0;//x output
                        blockListContent[x, y, z, 6] = 1;//y output
                        blockListContent[x, y, z, 7] = 0;//z output
                        //pulls from below, pumps straight up
                    }
                }
            }
            else
            {
                ConsoleWrite("clicked " + btn + " on random block!");
            }
           //do stuff 
        }

        public void DepositOre(Player player)
        {
            uint depositAmount = Math.Min(50, player.Ore);
            player.Ore -= depositAmount;
            if (player.Team == PlayerTeam.Red)
                teamOreRed = Math.Min(teamOreRed + depositAmount, 9999);
            else
                teamOreBlue = Math.Min(teamOreBlue + depositAmount, 9999);
        }

        public void WithdrawOre(Player player)
        {
            if (player.Team == PlayerTeam.Red)
            {
                uint withdrawAmount = Math.Min(player.OreMax - player.Ore, Math.Min(50, teamOreRed));
                player.Ore += withdrawAmount;
                teamOreRed -= withdrawAmount;
            }
            else
            {
                uint withdrawAmount = Math.Min(player.OreMax - player.Ore, Math.Min(50, teamOreBlue));
                player.Ore += withdrawAmount;
                teamOreBlue -= withdrawAmount;
            }
        }

        public void GetItem(Player player,string ID)
        {
            if (player.Alive)
            {
                
                foreach (KeyValuePair<string, Item> bPair in itemList)
                {
                    if (bPair.Value.ID == ID)
                    {
                       
                        if (Distf(player.Position, bPair.Value.Position) < 1.0)
                        {
                            itemList.Remove(bPair.Key);
                            SendSetItem(bPair.Key);
                            player.Cash = player.Cash + 5;
                            DepositCash(player);
                        }

                        foreach (Player p in playerList.Values)
                            SendResourceUpdate(p);

                        return;
                    }
                }
            }
        }
        public void DepositCash(Player player)
        {
            if (player.Cash <= 0)
                return;

            player.Score += player.Cash;

            if (!varGetB("sandbox"))
            {
                if (player.Team == PlayerTeam.Red)
                    teamCashRed += player.Cash;
                else
                    teamCashBlue += player.Cash;
                SendServerMessage("SERVER: " + player.Handle + " HAS EARNED $" + player.Cash + " FOR THE " + GetTeamName(player.Team) + " TEAM!");
            }

            PlaySound(InfiniminerSound.CashDeposit, player.Position);
            ConsoleWrite("DEPOSIT_CASH: " + player.Handle + ", " + player.Cash);
            
            player.Cash = 0;
            player.Weight = 0;

            foreach (Player p in playerList.Values)
                SendResourceUpdate(p);
        }

        public string GetTeamName(PlayerTeam team)
        {
            switch (team)
            {
                case PlayerTeam.Red:
                    return "RED";
                case PlayerTeam.Blue:
                    return "BLUE";
            }
            return "";
        }

        public void SendServerMessageToPlayer(string message, NetConnection conn)
        {
            if (conn.Status == NetConnectionStatus.Connected)
            {
                NetBuffer msgBuffer = netServer.CreateBuffer();
                msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);
                msgBuffer.Write((byte)ChatMessageType.SayAll);
                msgBuffer.Write(Defines.Sanitize(message));

                netServer.SendMessage(msgBuffer, conn, NetChannel.ReliableInOrder3);
            }
        }

        public void SendServerMessage(string message)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);
            msgBuffer.Write((byte)ChatMessageType.SayAll);
            msgBuffer.Write(Defines.Sanitize(message));
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder3);
        }

        // Lets a player know about their resources.
        public void SendResourceUpdate(Player player)
        {
            if (player.NetConn.Status != NetConnectionStatus.Connected)
                return;

            // ore, cash, weight, max ore, max weight, team ore, red cash, blue cash, all uint
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.ResourceUpdate);
            msgBuffer.Write((uint)player.Ore);
            msgBuffer.Write((uint)player.Cash);
            msgBuffer.Write((uint)player.Weight);
            msgBuffer.Write((uint)player.OreMax);
            msgBuffer.Write((uint)player.WeightMax);
            msgBuffer.Write((uint)(player.Team == PlayerTeam.Red ? teamOreRed : teamOreBlue));
            msgBuffer.Write((uint)teamCashRed);
            msgBuffer.Write((uint)teamCashBlue);
            msgBuffer.Write((uint)player.Health);
            msgBuffer.Write((uint)player.HealthMax);
            netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder1);
        }

        List<MapSender> mapSendingProgress = new List<MapSender>();

        public void TerminateFinishedThreads()
        {
            List<MapSender> mapSendersToRemove = new List<MapSender>();
            foreach (MapSender ms in mapSendingProgress)
            {
                if (ms.finished)
                {
                    ms.stop();
                    mapSendersToRemove.Add(ms);
                }
            }
            foreach (MapSender ms in mapSendersToRemove)
            {
                mapSendingProgress.Remove(ms);
            }
        }

        public void SendCurrentMap(NetConnection client)
        {
            MapSender ms = new MapSender(client, this, netServer, MAPSIZE,playerList[client].compression);
            mapSendingProgress.Add(ms);
        }

        /*public void SendCurrentMapB(NetConnection client)
        {
            Debug.Assert(MAPSIZE == 64, "The BlockBulkTransfer message requires a map size of 64.");
            
            for (byte x = 0; x < MAPSIZE; x++)
                for (byte y=0; y<MAPSIZE; y+=16)
                {
                    NetBuffer msgBuffer = netServer.CreateBuffer();
                    msgBuffer.Write((byte)InfiniminerMessage.BlockBulkTransfer);
                    msgBuffer.Write(x);
                    msgBuffer.Write(y);
                    for (byte dy=0; dy<16; dy++)
                        for (byte z = 0; z < MAPSIZE; z++)
                            msgBuffer.Write((byte)(blockList[x, y+dy, z]));
                    if (client.Status == NetConnectionStatus.Connected)
                        netServer.SendMessage(msgBuffer, client, NetChannel.ReliableUnordered);
                }
        }*/

        public void SendPlayerPing(uint playerId)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerPing);
            msgBuffer.Write(playerId);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
        }

        public void SendPlayerUpdate(Player player)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerUpdate);
            msgBuffer.Write((uint)player.ID);
            msgBuffer.Write(player.Position);
            msgBuffer.Write(player.Heading);
            msgBuffer.Write((byte)player.Tool);

            if (player.QueueAnimationBreak)
            {
                player.QueueAnimationBreak = false;
                msgBuffer.Write(false);
            }
            else
                msgBuffer.Write(player.UsingTool);

            msgBuffer.Write((ushort)player.Score / 100);
            msgBuffer.Write((ushort)player.Health / 100);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.UnreliableInOrder1);
        }

        public void SendSetBeacon(Vector3 position, string text, PlayerTeam team)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.SetBeacon);
            msgBuffer.Write(position);
            msgBuffer.Write(text);
            msgBuffer.Write((byte)team);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);
        }

        public void SendSetItem(string text, Vector3 position, PlayerTeam team, Vector3 heading)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.SetItem);
            msgBuffer.Write(text);
            msgBuffer.Write(position);
            msgBuffer.Write((byte)team);
            msgBuffer.Write(heading);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);
        }
        public void SendSetItem(string text)//empty item with no heading
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.SetItemRemove);
            msgBuffer.Write(text);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);
        }
        public void SendPlayerJoined(Player player)
        {
            NetBuffer msgBuffer;

            // Let this player know about other players.
            foreach (Player p in playerList.Values)
            {
                msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.PlayerJoined);
                msgBuffer.Write((uint)p.ID);
                msgBuffer.Write(p.Handle);
                msgBuffer.Write(p == player);
                msgBuffer.Write(p.Alive);
                if (player.NetConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder2);

                msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.PlayerSetTeam);
                msgBuffer.Write((uint)p.ID);
                msgBuffer.Write((byte)p.Team);
                if (player.NetConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder2);
            }

            // Let this player know about all placed beacons.
            foreach (KeyValuePair<string, Item> bPair in itemList)
            {
                Vector3 position = bPair.Value.Position;
                Vector3 heading = bPair.Value.Heading;
                position.Y += 1; //fixme
                msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.SetItem);
                msgBuffer.Write(bPair.Key);
                msgBuffer.Write(position);
                msgBuffer.Write((byte)bPair.Value.Team);
                msgBuffer.Write(heading);

                if (player.NetConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder2);
            }

            foreach (KeyValuePair<Vector3, Beacon> bPair in beaconList)
            {
                Vector3 position = bPair.Key;
                position.Y += 1; //fixme
                msgBuffer = netServer.CreateBuffer();
                msgBuffer.Write((byte)InfiniminerMessage.SetBeacon);
                msgBuffer.Write(position);
                msgBuffer.Write(bPair.Value.ID);
                msgBuffer.Write((byte)bPair.Value.Team);

                if (player.NetConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, player.NetConn, NetChannel.ReliableInOrder2);
            }

            // Let other players know about this player.
            msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerJoined);
            msgBuffer.Write((uint)player.ID);
            msgBuffer.Write(player.Handle);
            msgBuffer.Write(false);
            msgBuffer.Write(player.Alive);

            foreach (NetConnection netConn in playerList.Keys)
                if (netConn != player.NetConn && netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);

            // Send this out just incase someone is joining at the last minute.
            if (winningTeam != PlayerTeam.None)
                BroadcastGameOver();

            // Send out a chat message.
            msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);
            msgBuffer.Write((byte)ChatMessageType.SayAll);
            msgBuffer.Write(player.Handle + " HAS JOINED THE ADVENTURE!");
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder3);
        }

        public void BroadcastGameOver()
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.GameOver);
            msgBuffer.Write((byte)winningTeam);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);     
        }

        public void SendPlayerLeft(Player player, string reason)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerLeft);
            msgBuffer.Write((uint)player.ID);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn != player.NetConn && netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);

            // Send out a chat message.
            msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.ChatMessage);
            msgBuffer.Write((byte)ChatMessageType.SayAll);
            msgBuffer.Write(player.Handle + " " + reason);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder3);
        }

        public void SendPlayerSetTeam(Player player)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerSetTeam);
            msgBuffer.Write((uint)player.ID);
            msgBuffer.Write((byte)player.Team);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);
        }

        public void SendPlayerDead(Player player)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerDead);
            msgBuffer.Write((uint)player.ID);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);
        }

        public void SendPlayerAlive(Player player)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlayerAlive);
            msgBuffer.Write((uint)player.ID);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableInOrder2);
        }

        public void PlaySound(InfiniminerSound sound, Vector3 position)
        {
            NetBuffer msgBuffer = netServer.CreateBuffer();
            msgBuffer.Write((byte)InfiniminerMessage.PlaySound);
            msgBuffer.Write((byte)sound);
            msgBuffer.Write(true);
            msgBuffer.Write(position);
            foreach (NetConnection netConn in playerList.Keys)
                if (netConn.Status == NetConnectionStatus.Connected)
                    netServer.SendMessage(msgBuffer, netConn, NetChannel.ReliableUnordered);
        }

        Thread updater;
        bool updated = true;

        public void CommitUpdate()
        {
            try
            {
                if (updated)
                {
                    if (updater != null && !updater.IsAlive)
                    {
                        updater.Abort();
                        updater.Join();
                    }
                    updated = false;
                    updater = new Thread(new ThreadStart(this.RunUpdateThread));
                    updater.Start();
                }
            }
            catch { }
        }

        public void RunUpdateThread()
        {
            if (!updated)
            {
                Dictionary<string, string> postDict = new Dictionary<string, string>();
                postDict["name"] = varGetS("name");
                postDict["game"] = "INFINIMINER";
                postDict["player_count"] = "" + playerList.Keys.Count;
                postDict["player_capacity"] = "" + varGetI("maxplayers");
                postDict["extra"] = GetExtraInfo();

                lastServerListUpdate = DateTime.Now;

                try
                {
                    HttpRequest.Post("http://apps.keithholman.net/post", postDict);
                    ConsoleWrite("PUBLICLIST: UPDATING SERVER LISTING");
                }
                catch (Exception)
                {
                    ConsoleWrite("PUBLICLIST: ERROR CONTACTING SERVER");
                }

                updated = true;
            }
        }
    }
}