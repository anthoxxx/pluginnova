using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Life;
using Life.DB;
using Life.Network;
using Mirror;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SQLite;
using UnityEngine;

namespace MyIntranetOS
{
    /// <summary>
    /// MyIntranet OS : une tablette intranet accessible depuis le navigateur du PC en jeu (F2).
    /// Le plugin héberge son propre serveur web (aucune dépendance externe) qui sert l'interface,
    /// les applications déposées dans Applications/ et une API JSON pour ces applications.
    /// </summary>
    public class MyIntranetPlugin : Plugin
    {
        public const string Name = "MyIntranet OS";
        public const string Version = "1.0.0";

        internal static MyIntranetPlugin Instance;

        internal IntranetConfig Config;
        internal string DataDir;
        internal Database Db;
        internal PlayerDirectory Players;
        internal SessionManager Sessions;
        internal AppRegistry Apps;
        internal IGameBridge Game;
        internal Features Features;

        private HttpServer http;
        private bool homePageApplied;

        // Connexion -> SteamId, pour retrouver le joueur à la déconnexion (thread principal uniquement)
        private readonly Dictionary<NetworkConnection, ulong> connections = new Dictionary<NetworkConnection, ulong>();

        public MyIntranetPlugin(IGameAPI api) : base(api) { }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            Instance = this;

            try
            {
                DataDir = Path.Combine(pluginsPath ?? ".", "MyIntranetOS");
                Directory.CreateDirectory(DataDir);

                Config = IntranetConfig.LoadOrCreate(Path.Combine(DataDir, "Configuration", "configuration.json"));
                ResourceExtractor.ExtractBuiltins(DataDir);

                Db = new Database(Path.Combine(DataDir, "myintranet.db"));
                Players = new PlayerDirectory();
                Sessions = new SessionManager(this);
                Apps = new AppRegistry(Path.Combine(DataDir, "Applications"));
                Apps.Reload();
            }
            catch (Exception e)
            {
                Log.Error($"échec de l'initialisation : {e}");
                return;
            }

            // Le serveur web démarre d'abord : la tablette reste accessible même si une fonction du jeu échoue
            try
            {
                http = new HttpServer(Config.BindAddress, Config.Port, new Router(this).Handle);
                http.Start();
            }
            catch (Exception e)
            {
                Log.Error($"impossible d'ouvrir le port {Config.Port} ({e.Message}). Le port est-il déjà utilisé ? Changez \"Port\" dans Configuration/configuration.json.");
            }

            try
            {
                MainThread.Init();
                Game = new NovaGameBridge();
                Features = new Features(this, Game);
            }
            catch (Exception e)
            {
                Log.Error($"applications reliées au jeu indisponibles : {e}");
            }

            try
            {
                Nova.server.OnMinutePassedEvent += RefreshAllPlayers;
                Nova.server.OnPlayerMoneyEvent += OnMoneyChanged;
                Nova.server.OnPlayerBankEvent += OnBankChanged;
            }
            catch (Exception e)
            {
                Log.Error($"abonnement aux évènements du serveur impossible : {e.Message}");
            }

            RegisterCommands();
            TryApplyBrowserHomePage();

            Log.Info($"v{Version} initialisé : {Apps.All.Count} application(s), serveur web sur le port {Config.Port} ({Config.DisplayUrl})");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            if (Players == null) return;

            connections[conn] = player.steamId;
            Players.Update(player);
            // Un changement de personnage invalide les sessions de l'ancien personnage
            Sessions.RevokeOtherCharacters(player.steamId, character.Id);
            TryApplyBrowserHomePage();
            DeliverPendingCredits(player);
        }

        /// <summary>Crédite les virements reçus pendant que le joueur était hors ligne.</summary>
        private void DeliverPendingCredits(Player player)
        {
            try
            {
                if (Features == null) return;
                foreach (PendingCredit credit in Features.TakePendingCredits(player.character.Id))
                {
                    player.AddBankMoney(credit.Amount, credit.Reason);
                    player.Notify(Config.TabletName, $"{credit.Reason} : +{credit.Amount:N2} €", NotificationManager.Type.Success, 8f);
                }
            }
            catch (Exception e)
            {
                Log.Error($"virements en attente non crédités : {e}");
            }
        }

        private void OnBankChanged(Player player, double amount, string reason)
        {
            Players.Update(player);
            if (player?.character != null) Features?.LogBank(player.character.Id, amount, reason);
        }

        public override void OnPlayerDisconnect(NetworkConnection conn)
        {
            base.OnPlayerDisconnect(conn);
            if (Players == null) return;

            if (connections.TryGetValue(conn, out ulong steamId))
            {
                connections.Remove(conn);
                Players.Remove(steamId);
                Sessions.RevokePlayer(steamId);
            }
        }

        private void OnMoneyChanged(Player player, double amount, string reason)
        {
            Players.Update(player);
        }

        private void RefreshAllPlayers()
        {
            Players.RefreshAll(Nova.server.GetAllInGamePlayers());
            Sessions.Cleanup();
        }

        /// <summary>Option : définit la page d'accueil du navigateur en jeu sur la tablette.</summary>
        private void TryApplyBrowserHomePage()
        {
            if (homePageApplied || !Config.SetAsBrowserHomePage) return;
            try
            {
                if (WebManager.instance == null) return;
                WebManager.instance.NetworkhomePage = Config.DisplayUrl;
                homePageApplied = true;
                Log.Info($"page d'accueil du navigateur définie sur {Config.DisplayUrl}");
            }
            catch (Exception e)
            {
                homePageApplied = true;
                Log.Warning($"impossible de définir la page d'accueil du navigateur : {e.Message}");
            }
        }

        private void RegisterCommands()
        {
            SChatCommand command = new SChatCommand(Config.ChatCommand, Config.ChatCommandAliases ?? new string[0],
                "Ouvrir sa tablette MyIntranet OS", $"{Config.ChatCommand} [code|resetcode|deconnexion|reload]",
                (Action<Player, string[]>)OnTabletCommand);
            command.Register();
        }

        private void OnTabletCommand(Player player, string[] args)
        {
            Players.Update(player);
            string arg = args != null && args.Length > 0 ? args[0].Trim() : "";

            if (arg.Length == 0)
            {
                player.SendText($"<color=#0a84ff>{Config.TabletName}</color> : ouvrez le navigateur du PC (F2) et allez sur <b>{Config.DisplayUrl}</b>");
                player.SendText($"Si la tablette affiche un code, tapez <b>{Config.ChatCommand} &lt;code&gt;</b>.");
                return;
            }

            if (Regex.IsMatch(arg, "^[0-9]{6}$"))
            {
                if (Sessions.BindPairing(arg, player))
                    player.Notify(Config.TabletName, "Tablette associée à votre personnage.", NotificationManager.Type.Success, 5f);
                else
                    player.Notify(Config.TabletName, "Code invalide ou expiré.", NotificationManager.Type.Error, 5f);
                return;
            }

            switch (arg.ToLowerInvariant())
            {
                case "resetcode":
                    Db.ResetPin(player.steamId, player.character.Id);
                    player.Notify(Config.TabletName, "Le code de verrouillage de votre tablette a été supprimé.", NotificationManager.Type.Success, 5f);
                    break;

                case "deconnexion":
                case "logout":
                    Sessions.RevokePlayer(player.steamId);
                    player.Notify(Config.TabletName, "Toutes vos sessions de tablette ont été fermées.", NotificationManager.Type.Info, 5f);
                    break;

                case "reload":
                    if (!player.IsAdmin)
                    {
                        player.Notify(Config.TabletName, "Réservé au staff.", NotificationManager.Type.Error, 5f);
                        return;
                    }
                    Apps.Reload();
                    player.Notify(Config.TabletName, $"{Apps.All.Count} application(s) chargée(s).", NotificationManager.Type.Success, 5f);
                    break;

                default:
                    player.Notify(Config.TabletName, $"Usage : {Config.ChatCommand} [code|resetcode|deconnexion]", NotificationManager.Type.Warning, 5f);
                    break;
            }
        }
    }

    internal static class Log
    {
        public static void Info(string message) => Debug.Log($"[{MyIntranetPlugin.Name}] {message}");
        public static void Warning(string message) => Debug.LogWarning($"[{MyIntranetPlugin.Name}] {message}");
        public static void Error(string message) => Debug.LogError($"[{MyIntranetPlugin.Name}] {message}");
    }

    internal static class Clock
    {
        public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}

namespace MyIntranetOS
{
    /// <summary>Configuration/configuration.json</summary>
    public class IntranetConfig
    {
        public int Port = 8090;
        public string BindAddress = "0.0.0.0";
        public string PublicUrl = "";
        public string TabletName = "MyIntranet OS";
        public string ChatCommand = "/tablette";
        public string[] ChatCommandAliases = { "/intranet" };
        public bool AutoLoginByIp = true;
        public bool PairingCodeEnabled = true;
        public int PairingCodeLifetimeSeconds = 300;
        public int SessionLifetimeHours = 12;
        public int PinMaxAttempts = 5;
        public int PinLockoutSeconds = 300;
        public bool TrustProxyHeaders = false;
        public bool SetAsBrowserHomePage = false;
        public bool ShowMoney = true;

        public bool BankTransfersEnabled = true;
        public double BankTransferMax = 50000;
        public int BankTransferCooldownSeconds = 5;
        public int[] NewsPublisherBizIds = new int[0];
        public string[] NewsPublisherActivities = { "LawEnforcement" };
        public bool NewsNotifyPlayers = true;
        public int AdsLifetimeDays = 7;
        public int AdsMaxPerPlayer = 5;
        public int ServiceRequestLifetimeMinutes = 30;

        /// <summary>
        /// Services joignables depuis la tablette. Activities : None, LawEnforcement, Medical, Mecanic, Bus, Trash,
        /// Taxi, Transport, Hairdresser, Chef, Electrician, Fourriere, DIR, Agricole. BizIds : identifiants d'entreprises.
        /// </summary>
        public List<ServiceConfig> Services = new List<ServiceConfig>
        {
            new ServiceConfig { Id = "taxi", Name = "Taxi", Activities = new[] { "Taxi" }, RequestLabel = "Commander un taxi", Placeholder = "Destination, nombre de passagers…" },
            new ServiceConfig { Id = "bus", Name = "Bus", Activities = new[] { "Bus" }, RequestLabel = "Signaler un arrêt", Placeholder = "Arrêt, destination…" },
            new ServiceConfig { Id = "livraison", Name = "Blue Eats", Activities = new[] { "Chef" }, RequestLabel = "Commander", Placeholder = "Votre commande…" },
            new ServiceConfig { Id = "sante", Name = "Santé", Activities = new[] { "Medical" }, RequireOnDuty = false, RequestLabel = "Appeler les secours", Placeholder = "Que se passe-t-il ?" },
            new ServiceConfig { Id = "assurances", Name = "Assurances", RequireOnDuty = false, RequestLabel = "Être rappelé", Placeholder = "Votre besoin (auto, habitation, santé…)" },
            new ServiceConfig { Id = "avocats", Name = "Avocats", RequireOnDuty = false, RequestLabel = "Prendre rendez-vous", Placeholder = "Votre affaire…" },
            new ServiceConfig { Id = "grossiste", Name = "Grossiste", Activities = new[] { "Transport" }, RequireOnDuty = false, RequestLabel = "Passer commande", Placeholder = "Produits et quantités…" },
            new ServiceConfig { Id = "agriculteurs", Name = "Agriculteurs", Activities = new[] { "Agricole" }, RequireOnDuty = false, RequestLabel = "Passer commande", Placeholder = "Produits et quantités…" },
            new ServiceConfig { Id = "servicepublic", Name = "Service Public", Activities = new[] { "LawEnforcement" }, RequireOnDuty = false, RequestLabel = "Faire une démarche", Placeholder = "Votre démarche (papiers, plainte, rendez-vous…)" },
            new ServiceConfig { Id = "depannage", Name = "Dépannage", Activities = new[] { "Mecanic", "Fourriere" }, RequestLabel = "Demander un dépanneur", Placeholder = "Véhicule, panne…" }
        };

        public List<RadioStation> RadioStations = new List<RadioStation>
        {
            new RadioStation { Name = "France Inter", Url = "https://icecast.radiofrance.fr/franceinter-midfi.mp3" },
            new RadioStation { Name = "franceinfo", Url = "https://icecast.radiofrance.fr/franceinfo-midfi.mp3" },
            new RadioStation { Name = "FIP", Url = "https://icecast.radiofrance.fr/fip-midfi.mp3" },
            new RadioStation { Name = "Mouv'", Url = "https://icecast.radiofrance.fr/mouv-midfi.mp3" }
        };

        [JsonIgnore]
        public string DisplayUrl => string.IsNullOrWhiteSpace(PublicUrl) ? $"http://<ip-du-serveur>:{Port}/" : PublicUrl.Trim();

        public static IntranetConfig LoadOrCreate(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            IntranetConfig config = null;
            if (File.Exists(path))
            {
                // Replace : les listes du fichier remplacent les valeurs par défaut au lieu de s'y ajouter
                JsonSerializerSettings settings = new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace };
                try { config = JsonConvert.DeserializeObject<IntranetConfig>(File.ReadAllText(path), settings); }
                catch (Exception e) { Log.Error($"configuration.json invalide, valeurs par défaut utilisées : {e.Message}"); }
            }
            bool created = config == null && !File.Exists(path);
            config = config ?? new IntranetConfig();
            config.Sanitize();

            // Réécrit le fichier pour y ajouter les nouvelles options (les valeurs existantes sont conservées)
            try { File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented)); }
            catch (Exception e) { Log.Warning($"impossible d'écrire {path} : {e.Message}"); }
            if (created) Log.Info($"configuration créée : {path}");
            return config;
        }

        private void Sanitize()
        {
            if (Port < 1 || Port > 65535) Port = 8090;
            if (string.IsNullOrWhiteSpace(BindAddress)) BindAddress = "0.0.0.0";
            if (string.IsNullOrWhiteSpace(TabletName)) TabletName = "MyIntranet OS";
            if (string.IsNullOrWhiteSpace(ChatCommand)) ChatCommand = "/tablette";
            if (!ChatCommand.StartsWith("/")) ChatCommand = "/" + ChatCommand;
            PairingCodeLifetimeSeconds = Math.Max(30, PairingCodeLifetimeSeconds);
            SessionLifetimeHours = Math.Max(1, SessionLifetimeHours);
            PinMaxAttempts = Math.Max(1, PinMaxAttempts);
            PinLockoutSeconds = Math.Max(10, PinLockoutSeconds);
        }
    }

    /// <summary>
    /// Installe les applications intégrées et la documentation embarquées dans la DLL.
    /// Réglages est toujours réinstallée. Les autres applications sont installées une fois puis mises à jour
    /// quand leur version change ; une application supprimée par l'administrateur n'est pas réinstallée,
    /// et un dossier existant qui n'a pas été installé par le plugin n'est jamais écrasé.
    /// </summary>
    internal static class ResourceExtractor
    {
        private const string Custom = "custom";

        public static void ExtractBuiltins(string dataDir)
        {
            string appsDir = Path.Combine(dataDir, "Applications");
            Directory.CreateDirectory(appsDir);
            string statePath = Path.Combine(dataDir, "Configuration", "builtin-apps.json");
            Dictionary<string, string> installed = new Dictionary<string, string>();
            try
            {
                if (File.Exists(statePath))
                    installed = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(statePath)) ?? installed;
            }
            catch { }

            foreach (IGrouping<string, string> app in EmbeddedFiles.Names.Where(n => n.StartsWith("Apps/")).GroupBy(n => n.Split('/')[1]))
            {
                string id = app.Key;
                string folder = Path.Combine(appsDir, id);
                string version = EmbeddedVersion(id);
                bool exists = Directory.Exists(folder);
                bool known = installed.TryGetValue(id, out string installedVersion);

                bool write;
                if (id == AppRegistry.SettingsAppId) write = true;
                else if (!known) write = !exists;
                else write = exists && installedVersion != Custom && installedVersion != version;

                if (write)
                {
                    foreach (string name in app)
                    {
                        string target = Path.Combine(appsDir, name.Substring(5).Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.WriteAllBytes(target, EmbeddedFiles.Get(name));
                    }
                    installed[id] = version;
                }
                else if (!known)
                {
                    installed[id] = Custom;
                }
            }

            foreach (string name in EmbeddedFiles.Names.Where(n => n.StartsWith("Docs/")))
                File.WriteAllBytes(Path.Combine(dataDir, name.Substring(5)), EmbeddedFiles.Get(name));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(statePath));
                File.WriteAllText(statePath, JsonConvert.SerializeObject(installed, Formatting.Indented));
            }
            catch (Exception e) { Log.Warning($"impossible d'écrire {statePath} : {e.Message}"); }
        }

        private static string EmbeddedVersion(string id)
        {
            try
            {
                byte[] json = EmbeddedFiles.Get($"Apps/{id}/app.json");
                return json == null ? "0" : JsonConvert.DeserializeObject<AppManifest>(Encoding.UTF8.GetString(json))?.Version ?? "0";
            }
            catch { return "0"; }
        }
    }

    /// <summary>Fichiers embarqués dans la DLL (interface, SDK, application Réglages, documentation).</summary>
    internal static class EmbeddedFiles
    {
        private static readonly Assembly Assembly = typeof(EmbeddedFiles).Assembly;
        private static readonly Dictionary<string, string> Map = Assembly.GetManifestResourceNames()
            .ToDictionary(n => n.Replace('\\', '/'), n => n);
        private static readonly ConcurrentDictionary<string, byte[]> Cache = new ConcurrentDictionary<string, byte[]>();

        public static IEnumerable<string> Names => Map.Keys;

        public static byte[] Get(string name)
        {
            if (!Map.TryGetValue(name, out string resource)) return null;
            return Cache.GetOrAdd(name, _ =>
            {
                using (Stream stream = Assembly.GetManifestResourceStream(resource))
                using (MemoryStream memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    return memory.ToArray();
                }
            });
        }
    }
}

namespace MyIntranetOS
{
    /// <summary>
    /// Copie des informations des joueurs connectés, mise à jour sur le thread principal du jeu
    /// pour que le serveur web (autre thread) n'accède jamais directement aux objets Unity.
    /// </summary>
    internal sealed class PlayerSnapshot
    {
        public ulong SteamId;
        public string SteamName;
        public int CharacterId;
        public string FirstName;
        public string LastName;
        public string PhoneNumber;
        public string Birthday;
        public int Level;
        public int Xp;
        public double Money;
        public double Bank;
        public int BizId;
        public string BizName;
        public bool IsAdmin;
        public string Ip;
        public long UpdatedAt;
        public string IdCard;
        public string Commune;
        public string Height;
        public int SexId;
        public bool PermisB;
        public int PermisPoints;
        public bool HasBCR;
        public bool OnDuty;

        public string FullName => $"{FirstName} {LastName}".Trim();
    }

    internal sealed class PlayerDirectory
    {
        private readonly ConcurrentDictionary<ulong, PlayerSnapshot> players = new ConcurrentDictionary<ulong, PlayerSnapshot>();

        public void Update(Player player)
        {
            if (player == null || player.character == null) return;
            try
            {
                Characters c = player.character;
                PlayerSnapshot snapshot = new PlayerSnapshot
                {
                    SteamId = player.steamId,
                    SteamName = player.steamUsername,
                    CharacterId = c.Id,
                    FirstName = c.Firstname,
                    LastName = c.Lastname,
                    PhoneNumber = c.PhoneNumber,
                    Birthday = c.Birthday,
                    Level = c.Level,
                    Xp = c.XP,
                    Bank = c.Bank,
                    BizId = c.BizId,
                    IsAdmin = player.IsAdmin,
                    Ip = IpUtils.Normalize(GetAddress(player)),
                    UpdatedAt = Clock.Now,
                    IdCard = c.IdCard,
                    Commune = c.Commune,
                    Height = c.Height,
                    SexId = c.SexId,
                    PermisB = c.PermisB,
                    PermisPoints = c.PermisPoints,
                    HasBCR = c.HasBCR,
                    OnDuty = player.serviceMetier
                };
                try { snapshot.Money = player.Money; } catch { }
                try { snapshot.BizName = player.HasBiz ? player.biz?.BizName : null; } catch { }
                players[player.steamId] = snapshot;
            }
            catch (Exception e)
            {
                Log.Warning($"lecture du joueur impossible : {e.Message}");
            }
        }

        public void RefreshAll(IEnumerable<Player> online)
        {
            HashSet<ulong> seen = new HashSet<ulong>();
            foreach (Player player in online ?? Enumerable.Empty<Player>())
            {
                if (player?.character == null) continue;
                Update(player);
                seen.Add(player.steamId);
            }
            foreach (ulong steamId in players.Keys.Where(id => !seen.Contains(id)).ToList())
                players.TryRemove(steamId, out _);
        }

        public void Remove(ulong steamId) => players.TryRemove(steamId, out _);

        public PlayerSnapshot Get(ulong steamId) => players.TryGetValue(steamId, out PlayerSnapshot p) ? p : null;

        public List<int> OnlineCharacterIds() => players.Values.Select(p => p.CharacterId).ToList();

        public List<PlayerSnapshot> FindByIp(string ip) => players.Values.Where(p => p.Ip != null && p.Ip == ip).ToList();

        private static string GetAddress(Player player)
        {
            try { return (player.conn as NetworkConnectionToClient)?.address; }
            catch { return null; }
        }
    }

    internal static class IpUtils
    {
        public static string Normalize(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            address = address.Trim();
            if (address == "localhost") return "127.0.0.1";

            if (!IPAddress.TryParse(address, out IPAddress ip))
            {
                // "ip:port"
                int colon = address.LastIndexOf(':');
                if (colon <= 0 || !IPAddress.TryParse(address.Substring(0, colon).Trim('[', ']'), out ip))
                    return address;
            }
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            if (IPAddress.IsLoopback(ip)) return "127.0.0.1";
            return ip.ToString();
        }
    }
}

namespace MyIntranetOS
{
    [Table("tablet_profiles")]
    public class TabletProfile
    {
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        [Unique] public string OwnerKey { get; set; }
        public string SteamId { get; set; }
        public int CharacterId { get; set; }

        public string Theme { get; set; } = "auto";
        public string Accent { get; set; } = "#0a84ff";
        public string Wallpaper { get; set; } = "aurora";
        public int Brightness { get; set; } = 100;
        public int TextScale { get; set; } = 100;
        public bool Use24h { get; set; } = true;
        public int AutoLockMinutes { get; set; } = 5;
        public string AppOrder { get; set; } = "[]";
        public string HiddenApps { get; set; } = "[]";
        public string Dock { get; set; } = "[\"portefeuille\",\"banque\",\"servicepublic\",\"birdy\",\"reglages\"]";
        public string Widgets { get; set; } = "[\"clock\",\"wallet\",\"notifications\",\"news\"]";

        public string PinHash { get; set; }
        public string PinSalt { get; set; }
        public int FailedAttempts { get; set; }
        public int Lockouts { get; set; }
        public long LockedUntil { get; set; }

        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }

        [Ignore] public bool HasPin => !string.IsNullOrEmpty(PinHash);
    }

    [Table("app_storage")]
    public class AppStorageEntry
    {
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        [Indexed] public string OwnerKey { get; set; }
        [Indexed] public string AppId { get; set; }
        public string Key { get; set; }
        public string Value { get; set; }
        public long UpdatedAt { get; set; }
    }

    /// <summary>Base SQLite propre au plugin (MyIntranetOS/myintranet.db).</summary>
    internal sealed class Database
    {
        private readonly SQLiteConnection db;
        private readonly object sync = new object();

        public Database(string path)
        {
            db = new SQLiteConnection(path);
            db.CreateTable<TabletProfile>();
            db.CreateTable<AppStorageEntry>();
        }

        public void Register<T>() where T : new()
        {
            lock (sync) db.CreateTable<T>();
        }

        /// <summary>Accès exclusif à la connexion (une seule requête SQLite à la fois).</summary>
        public T With<T>(Func<SQLiteConnection, T> action)
        {
            lock (sync) return action(db);
        }

        public static string OwnerKey(ulong steamId, int characterId) => $"{steamId}:{characterId}";

        public TabletProfile GetProfile(ulong steamId, int characterId)
        {
            string key = OwnerKey(steamId, characterId);
            lock (sync)
            {
                TabletProfile profile = db.Table<TabletProfile>().Where(p => p.OwnerKey == key).FirstOrDefault();
                if (profile != null) return profile;

                profile = new TabletProfile
                {
                    OwnerKey = key,
                    SteamId = steamId.ToString(),
                    CharacterId = characterId,
                    CreatedAt = Clock.Now,
                    UpdatedAt = Clock.Now
                };
                db.Insert(profile);
                return profile;
            }
        }

        public void Save(TabletProfile profile)
        {
            profile.UpdatedAt = Clock.Now;
            lock (sync) db.Update(profile);
        }

        public void ResetPin(ulong steamId, int characterId)
        {
            TabletProfile profile = GetProfile(steamId, characterId);
            profile.PinHash = null;
            profile.PinSalt = null;
            profile.FailedAttempts = 0;
            profile.Lockouts = 0;
            profile.LockedUntil = 0;
            Save(profile);
        }

        public List<AppStorageEntry> StorageList(string owner, string appId)
        {
            lock (sync) return db.Table<AppStorageEntry>().Where(e => e.OwnerKey == owner && e.AppId == appId).ToList();
        }

        public AppStorageEntry StorageGet(string owner, string appId, string key)
        {
            lock (sync) return db.Table<AppStorageEntry>().Where(e => e.OwnerKey == owner && e.AppId == appId && e.Key == key).FirstOrDefault();
        }

        public void StorageSet(string owner, string appId, string key, string value)
        {
            lock (sync)
            {
                AppStorageEntry entry = db.Table<AppStorageEntry>().Where(e => e.OwnerKey == owner && e.AppId == appId && e.Key == key).FirstOrDefault();
                if (entry == null)
                {
                    db.Insert(new AppStorageEntry { OwnerKey = owner, AppId = appId, Key = key, Value = value, UpdatedAt = Clock.Now });
                }
                else
                {
                    entry.Value = value;
                    entry.UpdatedAt = Clock.Now;
                    db.Update(entry);
                }
            }
        }

        public bool StorageDelete(string owner, string appId, string key)
        {
            lock (sync)
            {
                AppStorageEntry entry = db.Table<AppStorageEntry>().Where(e => e.OwnerKey == owner && e.AppId == appId && e.Key == key).FirstOrDefault();
                if (entry == null) return false;
                db.Delete<AppStorageEntry>(entry.Id);
                return true;
            }
        }
    }

    /// <summary>Code à 4 chiffres : haché (PBKDF2 + sel) et protégé contre la force brute.</summary>
    internal static class PinSecurity
    {
        public static bool IsValidPin(string pin) => pin != null && Regex.IsMatch(pin, "^[0-9]{4}$");

        public static void SetPin(TabletProfile profile, string pin)
        {
            if (string.IsNullOrEmpty(pin))
            {
                profile.PinHash = null;
                profile.PinSalt = null;
            }
            else
            {
                byte[] salt = Tokens.RandomBytes(16);
                profile.PinSalt = Convert.ToBase64String(salt);
                profile.PinHash = Hash(pin, salt);
            }
            profile.FailedAttempts = 0;
            profile.Lockouts = 0;
            profile.LockedUntil = 0;
        }

        /// <summary>Vérifie le code. Retourne false (sans vérifier) si le compte est temporairement bloqué.</summary>
        public static bool Check(TabletProfile profile, string pin, IntranetConfig config)
        {
            if (!profile.HasPin) return true;
            if (profile.LockedUntil > Clock.Now) return false;

            bool ok = IsValidPin(pin) && SlowEquals(Hash(pin, Convert.FromBase64String(profile.PinSalt)), profile.PinHash);
            if (ok)
            {
                profile.FailedAttempts = 0;
                profile.Lockouts = 0;
                profile.LockedUntil = 0;
                return true;
            }

            profile.FailedAttempts++;
            if (profile.FailedAttempts >= config.PinMaxAttempts)
            {
                // Blocage qui double à chaque récidive (plafonné à 1 h)
                long duration = Math.Min(3600L, (long)config.PinLockoutSeconds << Math.Min(profile.Lockouts, 10));
                profile.LockedUntil = Clock.Now + duration;
                profile.Lockouts++;
                profile.FailedAttempts = 0;
            }
            return false;
        }

        private static string Hash(string pin, byte[] salt)
        {
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(pin, salt, 20000))
                return Convert.ToBase64String(kdf.GetBytes(32));
        }

        private static bool SlowEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    internal static class Tokens
    {
        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        public static byte[] RandomBytes(int count)
        {
            byte[] bytes = new byte[count];
            lock (Rng) Rng.GetBytes(bytes);
            return bytes;
        }

        public static string NewToken() => BitConverter.ToString(RandomBytes(32)).Replace("-", "").ToLowerInvariant();

        public static string NewDigits(int length)
        {
            StringBuilder sb = new StringBuilder(length);
            foreach (byte b in RandomBytes(length * 2))
            {
                if (b >= 250) continue; // évite le biais du modulo
                sb.Append((char)('0' + b % 10));
                if (sb.Length == length) return sb.ToString();
            }
            return NewDigits(length);
        }
    }
}

namespace MyIntranetOS
{
    internal sealed class Session
    {
        public string Token;
        public ulong SteamId;
        public int CharacterId;
        public bool Unlocked;
        public long ExpiresAt;
        public string OwnerKey => Database.OwnerKey(SteamId, CharacterId);
    }

    internal sealed class Pairing
    {
        public string Token;
        public string Code;
        public long ExpiresAt;
        public ulong SteamId;
        public int CharacterId;
        public bool Bound;
    }

    /// <summary>
    /// Identification du joueur : la session web est rattachée au joueur connecté au serveur de jeu,
    /// soit automatiquement (même adresse IP que sa connexion de jeu), soit par un code à taper en jeu.
    /// Les sessions sont fermées dès que le joueur quitte le serveur.
    /// </summary>
    internal sealed class SessionManager
    {
        public const string SessionCookie = "mios_session";
        public const string PairingCookie = "mios_pair";

        private readonly MyIntranetPlugin plugin;
        private readonly ConcurrentDictionary<string, Session> sessions = new ConcurrentDictionary<string, Session>();
        private readonly ConcurrentDictionary<string, Pairing> pairings = new ConcurrentDictionary<string, Pairing>();

        public SessionManager(MyIntranetPlugin plugin)
        {
            this.plugin = plugin;
        }

        /// <summary>Session valide liée à un joueur toujours connecté, sinon null.</summary>
        public Session Get(string token)
        {
            if (string.IsNullOrEmpty(token) || !sessions.TryGetValue(token, out Session session)) return null;
            PlayerSnapshot player = plugin.Players.Get(session.SteamId);
            if (session.ExpiresAt < Clock.Now || player == null || player.CharacterId != session.CharacterId)
            {
                sessions.TryRemove(token, out _);
                return null;
            }
            return session;
        }

        public Session Create(PlayerSnapshot player)
        {
            Session session = new Session
            {
                Token = Tokens.NewToken(),
                SteamId = player.SteamId,
                CharacterId = player.CharacterId,
                ExpiresAt = Clock.Now + plugin.Config.SessionLifetimeHours * 3600L
            };
            sessions[session.Token] = session;
            return session;
        }

        public void Revoke(string token) => sessions.TryRemove(token ?? "", out _);

        public void RevokePlayer(ulong steamId)
        {
            foreach (Session s in sessions.Values.Where(s => s.SteamId == steamId).ToList())
                sessions.TryRemove(s.Token, out _);
            foreach (Pairing p in pairings.Values.Where(p => p.Bound && p.SteamId == steamId).ToList())
                pairings.TryRemove(p.Token, out _);
        }

        public void RevokeOtherCharacters(ulong steamId, int characterId)
        {
            foreach (Session s in sessions.Values.Where(s => s.SteamId == steamId && s.CharacterId != characterId).ToList())
                sessions.TryRemove(s.Token, out _);
        }

        /// <summary>Code d'association pour ce navigateur (créé ou réutilisé).</summary>
        public Pairing GetOrCreatePairing(string token)
        {
            if (!string.IsNullOrEmpty(token) && pairings.TryGetValue(token, out Pairing existing) && existing.ExpiresAt > Clock.Now)
                return existing;

            string code;
            do code = Tokens.NewDigits(6);
            while (pairings.Values.Any(p => p.Code == code));

            Pairing pairing = new Pairing
            {
                Token = Tokens.NewToken(),
                Code = code,
                ExpiresAt = Clock.Now + plugin.Config.PairingCodeLifetimeSeconds
            };
            pairings[pairing.Token] = pairing;
            return pairing;
        }

        /// <summary>Appelé par la commande de chat (thread principal).</summary>
        public bool BindPairing(string code, Player player)
        {
            Pairing pairing = pairings.Values.FirstOrDefault(p => p.Code == code && !p.Bound && p.ExpiresAt > Clock.Now);
            if (pairing == null || player.character == null) return false;
            pairing.SteamId = player.steamId;
            pairing.CharacterId = player.character.Id;
            pairing.Bound = true;
            return true;
        }

        /// <summary>Si le code de ce navigateur a été validé en jeu, crée la session.</summary>
        public Session ConsumePairing(string token)
        {
            if (string.IsNullOrEmpty(token) || !pairings.TryGetValue(token, out Pairing pairing) || !pairing.Bound) return null;
            pairings.TryRemove(token, out _);
            PlayerSnapshot player = plugin.Players.Get(pairing.SteamId);
            if (player == null || player.CharacterId != pairing.CharacterId) return null;
            return Create(player);
        }

        public void Cleanup()
        {
            long now = Clock.Now;
            foreach (Session s in sessions.Values.Where(s => s.ExpiresAt < now).ToList())
                sessions.TryRemove(s.Token, out _);
            foreach (Pairing p in pairings.Values.Where(p => p.ExpiresAt < now).ToList())
                pairings.TryRemove(p.Token, out _);
        }
    }
}

namespace MyIntranetOS
{
    public class AppManifest
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        [JsonProperty("description")] public string Description;
        [JsonProperty("version")] public string Version;
        [JsonProperty("author")] public string Author;
        [JsonProperty("icon")] public string Icon;
        [JsonProperty("color")] public string Color;
        [JsonProperty("entry")] public string Entry;
        [JsonProperty("adminOnly")] public bool AdminOnly;
        [JsonProperty("order")] public int Order = 1000;

        [JsonIgnore] public string Folder;
        [JsonIgnore] public bool Builtin => Id == AppRegistry.SettingsAppId;
    }

    /// <summary>Applications installées : un dossier par application dans Applications/, avec un app.json.</summary>
    internal sealed class AppRegistry
    {
        public const string SettingsAppId = "reglages";
        private static readonly Regex IdPattern = new Regex("^[a-z0-9][a-z0-9_-]{1,31}$");
        private static readonly Regex ColorPattern = new Regex("^#[0-9a-fA-F]{6}$");

        private readonly string root;
        private readonly object sync = new object();
        private List<AppManifest> apps = new List<AppManifest>();
        private DateTime lastScan = DateTime.MinValue;

        public AppRegistry(string root)
        {
            this.root = root;
        }

        /// <summary>Rescanne le dossier au plus toutes les 10 secondes : une app déposée apparaît sans redémarrage.</summary>
        public IReadOnlyList<AppManifest> All
        {
            get
            {
                if ((DateTime.UtcNow - lastScan).TotalSeconds > 10) Reload();
                return apps;
            }
        }

        public AppManifest Find(string id) => All.FirstOrDefault(a => a.Id == id);

        public void Reload()
        {
            lock (sync)
            {
                List<AppManifest> found = new List<AppManifest>();
                Directory.CreateDirectory(root);
                foreach (string dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    AppManifest app = Load(dir);
                    if (app == null) continue;
                    if (found.Any(a => a.Id == app.Id))
                    {
                        Log.Warning($"application ignorée ({dir}) : l'identifiant \"{app.Id}\" est déjà utilisé");
                        continue;
                    }
                    found.Add(app);
                }
                // Ordre par défaut : champ "order" du manifeste, puis nom
                apps = found.OrderBy(a => a.Order).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                lastScan = DateTime.UtcNow;
            }
        }

        private static AppManifest Load(string dir)
        {
            string manifestPath = Path.Combine(dir, "app.json");
            if (!File.Exists(manifestPath)) return null;
            try
            {
                AppManifest app = JsonConvert.DeserializeObject<AppManifest>(File.ReadAllText(manifestPath));
                if (app == null) return null;
                app.Folder = Path.GetFullPath(dir);
                app.Id = (string.IsNullOrWhiteSpace(app.Id) ? Path.GetFileName(dir) : app.Id).Trim().ToLowerInvariant();
                if (!IdPattern.IsMatch(app.Id))
                {
                    Log.Warning($"application ignorée ({dir}) : identifiant \"{app.Id}\" invalide (a-z, 0-9, - et _)");
                    return null;
                }
                app.Name = string.IsNullOrWhiteSpace(app.Name) ? app.Id : app.Name.Trim();
                app.Entry = string.IsNullOrWhiteSpace(app.Entry) ? "index.html" : app.Entry.Trim().TrimStart('/');
                app.Color = app.Color != null && ColorPattern.IsMatch(app.Color) ? app.Color : "#5e5ce6";
                if (ResolveFile(app, app.Entry) == null)
                {
                    Log.Warning($"application ignorée ({dir}) : fichier \"{app.Entry}\" introuvable");
                    return null;
                }
                return app;
            }
            catch (Exception e)
            {
                Log.Warning($"app.json invalide ({dir}) : {e.Message}");
                return null;
            }
        }

        /// <summary>Chemin d'un fichier de l'application, ou null s'il sort du dossier ou n'existe pas.</summary>
        public static string ResolveFile(AppManifest app, string relative)
        {
            if (string.IsNullOrEmpty(relative) || relative.Contains("..") || relative.Contains(":")) return null;
            string full = Path.GetFullPath(Path.Combine(app.Folder, relative.Replace('/', Path.DirectorySeparatorChar)));
            string folder = app.Folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return null;
            return File.Exists(full) ? full : null;
        }

        /// <summary>Icône : image du dossier de l'app, ou texte/emoji.</summary>
        public static object IconOf(AppManifest app)
        {
            string icon = app.Icon?.Trim();
            if (string.IsNullOrEmpty(icon)) return new { type = "text", value = app.Name.Substring(0, 1).ToUpperInvariant() };
            if (Regex.IsMatch(icon, @"\.(png|jpe?g|gif|svg|webp)$", RegexOptions.IgnoreCase) && ResolveFile(app, icon) != null)
                return new { type = "image", value = $"/apps/{app.Id}/{icon.TrimStart('/')}" };
            return new { type = "text", value = icon.Length > 8 ? icon.Substring(0, 8) : icon };
        }
    }
}

namespace MyIntranetOS
{
    internal sealed class HttpRequest
    {
        public string Method;
        public string Path;
        public Dictionary<string, string> Query = new Dictionary<string, string>();
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];
        public string RemoteIp;

        public string Header(string name) => Headers.TryGetValue(name, out string v) ? v : null;
        public string BodyText => Encoding.UTF8.GetString(Body);

        public string Cookie(string name)
        {
            string header = Header("Cookie");
            if (header == null) return null;
            foreach (string part in header.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq).Trim() == name) return part.Substring(eq + 1).Trim();
            }
            return null;
        }
    }

    internal sealed class HttpResponse
    {
        public int Status = 200;
        public string ContentType = "text/plain; charset=utf-8";
        public byte[] Body = new byte[0];
        public List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();

        public HttpResponse AddHeader(string name, string value)
        {
            Headers.Add(new KeyValuePair<string, string>(name, value));
            return this;
        }

        public static HttpResponse Json(object value, int status = 200) => new HttpResponse
        {
            Status = status,
            ContentType = "application/json; charset=utf-8",
            Body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value))
        }.AddHeader("Cache-Control", "no-store");

        public static HttpResponse Error(int status, string error, string message = null) =>
            Json(new { error, message }, status);

        public static HttpResponse File(byte[] content, string path) => new HttpResponse
        {
            ContentType = MimeTypes.Get(path),
            Body = content
        }.AddHeader("Cache-Control", "no-cache");
    }

    internal static class MimeTypes
    {
        private static readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".html", "text/html; charset=utf-8" }, { ".htm", "text/html; charset=utf-8" },
            { ".css", "text/css; charset=utf-8" }, { ".js", "application/javascript; charset=utf-8" },
            { ".json", "application/json; charset=utf-8" }, { ".txt", "text/plain; charset=utf-8" },
            { ".md", "text/plain; charset=utf-8" }, { ".svg", "image/svg+xml" },
            { ".png", "image/png" }, { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" },
            { ".gif", "image/gif" }, { ".webp", "image/webp" }, { ".ico", "image/x-icon" },
            { ".woff", "font/woff" }, { ".woff2", "font/woff2" }, { ".ttf", "font/ttf" },
            { ".mp3", "audio/mpeg" }, { ".ogg", "audio/ogg" }, { ".wav", "audio/wav" }, { ".mp4", "video/mp4" }
        };

        public static string Get(string path) =>
            Types.TryGetValue(System.IO.Path.GetExtension(path) ?? "", out string type) ? type : "application/octet-stream";
    }

    /// <summary>Serveur HTTP/1.1 minimal sur TcpListener (pas d'http.sys, pas de droits administrateur requis).</summary>
    internal sealed class HttpServer
    {
        private const int MaxHeaderBytes = 16 * 1024;
        private const int MaxBodyBytes = 256 * 1024;
        private const int MaxConcurrent = 64;

        private readonly TcpListener listener;
        private readonly Func<HttpRequest, HttpResponse> handler;
        private readonly SemaphoreSlim slots = new SemaphoreSlim(MaxConcurrent);
        private volatile bool running;

        public HttpServer(string bindAddress, int port, Func<HttpRequest, HttpResponse> handler)
        {
            IPAddress address = bindAddress == "0.0.0.0" || bindAddress == "*" || bindAddress == "+"
                ? IPAddress.Any
                : IPAddress.Parse(bindAddress);
            listener = new TcpListener(address, port);
            this.handler = handler;
        }

        public void Start()
        {
            listener.Start(100);
            running = true;
            new Thread(AcceptLoop) { IsBackground = true, Name = "MyIntranetOS-HTTP" }.Start();
        }

        public void Stop()
        {
            running = false;
            try { listener.Stop(); } catch { }
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception e)
                {
                    if (running) Log.Warning($"erreur d'écoute HTTP : {e.Message}");
                    continue;
                }

                if (!slots.Wait(0))
                {
                    client.Close();
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Serve(client); }
                    catch { /* client parti, requête invalide... */ }
                    finally
                    {
                        slots.Release();
                        client.Close();
                    }
                });
            }
        }

        private void Serve(TcpClient client)
        {
            client.ReceiveTimeout = 10000;
            client.SendTimeout = 10000;
            NetworkStream stream = client.GetStream();

            HttpRequest request = Read(stream);
            if (request == null)
            {
                Write(stream, HttpResponse.Error(400, "bad_request"), false);
                return;
            }
            request.RemoteIp = IpUtils.Normalize(((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString());

            HttpResponse response;
            try { response = handler(request); }
            catch (Exception e)
            {
                Log.Error($"erreur sur {request.Method} {request.Path} : {e}");
                response = HttpResponse.Error(500, "server_error");
            }
            Write(stream, response, request.Method != "HEAD");
        }

        private static HttpRequest Read(NetworkStream stream)
        {
            byte[] buffer = new byte[8192];
            MemoryStream received = new MemoryStream();
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = stream.Read(buffer, 0, buffer.Length);
                if (n <= 0) return null;
                received.Write(buffer, 0, n);
                headerEnd = IndexOfHeaderEnd(received.GetBuffer(), (int)received.Length);
                if (headerEnd < 0 && received.Length > MaxHeaderBytes) return null;
            }

            byte[] data = received.ToArray();
            string[] lines = Encoding.ASCII.GetString(data, 0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] start = lines[0].Split(' ');
            if (start.Length != 3 || !start[2].StartsWith("HTTP/1.")) return null;

            HttpRequest request = new HttpRequest { Method = start[0].ToUpperInvariant() };
            string target = start[1];
            int q = target.IndexOf('?');
            request.Path = Uri.UnescapeDataString(q >= 0 ? target.Substring(0, q) : target);
            if (q >= 0)
            {
                foreach (string pair in target.Substring(q + 1).Split('&'))
                {
                    if (pair.Length == 0) continue;
                    int eq = pair.IndexOf('=');
                    string key = Uri.UnescapeDataString((eq >= 0 ? pair.Substring(0, eq) : pair).Replace('+', ' '));
                    string value = eq >= 0 ? Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' ')) : "";
                    request.Query[key] = value;
                }
            }

            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0) request.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            int length = 0;
            string contentLength = request.Header("Content-Length");
            if (contentLength != null && (!int.TryParse(contentLength, out length) || length < 0 || length > MaxBodyBytes)) return null;

            int bodyStart = headerEnd + 4;
            byte[] body = new byte[length];
            int have = Math.Min(length, data.Length - bodyStart);
            Array.Copy(data, bodyStart, body, 0, have);
            while (have < length)
            {
                int n = stream.Read(body, have, length - have);
                if (n <= 0) return null;
                have += n;
            }
            request.Body = body;
            return request;
        }

        private static int IndexOfHeaderEnd(byte[] data, int length)
        {
            for (int i = 0; i + 3 < length; i++)
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n') return i;
            return -1;
        }

        private static void Write(NetworkStream stream, HttpResponse response, bool withBody)
        {
            StringBuilder head = new StringBuilder();
            head.Append($"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\n");
            head.Append($"Content-Type: {response.ContentType}\r\n");
            head.Append($"Content-Length: {response.Body.Length}\r\n");
            head.Append("Connection: close\r\n");
            head.Append("X-Content-Type-Options: nosniff\r\n");
            head.Append("X-Frame-Options: SAMEORIGIN\r\n");
            head.Append("Referrer-Policy: same-origin\r\n");
            foreach (KeyValuePair<string, string> h in response.Headers) head.Append($"{h.Key}: {h.Value}\r\n");
            head.Append("\r\n");

            byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());
            stream.Write(headBytes, 0, headBytes.Length);
            if (withBody && response.Body.Length > 0) stream.Write(response.Body, 0, response.Body.Length);
            stream.Flush();
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 413: return "Payload Too Large";
                case 423: return "Locked";
                case 429: return "Too Many Requests";
                default: return status >= 500 ? "Internal Server Error" : "OK";
            }
        }
    }
}

namespace MyIntranetOS
{
    /// <summary>Routes du serveur web : interface, SDK, fichiers des applications et API JSON.</summary>
    internal sealed class Router
    {
        private static readonly Regex StorageKeyPattern = new Regex("^[A-Za-z0-9_.-]{1,64}$");
        private static readonly Regex AccentPattern = new Regex("^#[0-9a-fA-F]{6}$");
        private static readonly Regex WallpaperUrlPattern = new Regex(@"^https?://[^\s""'()\\<>]{1,500}$");
        private static readonly string[] WallpaperPresets = { "aurora", "ocean", "sunset", "forest", "night", "nova", "graphite", "candy" };
        private static readonly string[] Themes = { "auto", "light", "dark" };
        private static readonly string[] WidgetIds = { "clock", "wallet", "notifications", "news", "profile", "calendar" };
        private const int MaxStorageValueBytes = 64 * 1024;
        private const int MaxStorageKeysPerApp = 200;

        private readonly MyIntranetPlugin plugin;
        private readonly ConcurrentDictionary<string, Queue<long>> pinAttemptsByIp = new ConcurrentDictionary<string, Queue<long>>();
        // Vérifications de code sérialisées : des requêtes parallèles ne peuvent pas contourner le compteur d'essais
        private readonly object pinSync = new object();

        public Router(MyIntranetPlugin plugin)
        {
            this.plugin = plugin;
        }

        private IntranetConfig Config => plugin.Config;

        public HttpResponse Handle(HttpRequest req)
        {
            if (Config.TrustProxyHeaders)
            {
                string forwarded = req.Header("X-Forwarded-For");
                if (!string.IsNullOrEmpty(forwarded)) req.RemoteIp = IpUtils.Normalize(forwarded.Split(',')[0]);
            }

            string path = req.Path;
            if (path.StartsWith("/api/")) return HandleApi(req, path.Substring(4));
            if (req.Method != "GET" && req.Method != "HEAD") return HttpResponse.Error(405, "method_not_allowed");

            if (path == "/" || path == "/index.html") return Embedded("Web/index.html");
            if (path == "/favicon.ico") return new HttpResponse { Status = 204 };
            if (path.StartsWith("/os/")) return Embedded("Web/" + path.Substring(4));
            if (path.StartsWith("/sdk/")) return Embedded("Web/sdk/" + path.Substring(5));
            if (path.StartsWith("/apps/")) return AppFile(path.Substring(6));
            return HttpResponse.Error(404, "not_found");
        }

        private static HttpResponse Embedded(string name)
        {
            if (name.Contains("..")) return HttpResponse.Error(404, "not_found");
            byte[] content = EmbeddedFiles.Get(name);
            return content == null ? HttpResponse.Error(404, "not_found") : HttpResponse.File(content, name);
        }

        private HttpResponse AppFile(string rest)
        {
            int slash = rest.IndexOf('/');
            string id = slash < 0 ? rest : rest.Substring(0, slash);
            string file = slash < 0 ? "" : rest.Substring(slash + 1);

            AppManifest app = plugin.Apps.Find(id);
            if (app == null) return HttpResponse.Error(404, "not_found");
            if (file.Length == 0) file = app.Entry;

            string full = AppRegistry.ResolveFile(app, file);
            return full == null ? HttpResponse.Error(404, "not_found") : HttpResponse.File(File.ReadAllBytes(full), full);
        }

        // ------------------------------------------------------------------
        //  API
        // ------------------------------------------------------------------

        private HttpResponse HandleApi(HttpRequest req, string path)
        {
            bool write = req.Method == "POST" || req.Method == "PUT" || req.Method == "DELETE";
            if (!write && req.Method != "GET") return HttpResponse.Error(405, "method_not_allowed");
            // En-tête personnalisé obligatoire : un autre site ne peut pas l'envoyer sans pré-vérification CORS (refusée)
            if (write && req.Header("X-MyIntranet") != "1") return HttpResponse.Error(403, "forbidden", "En-tête X-MyIntranet manquant.");

            if (path == "/info" && req.Method == "GET") return Info();
            if (path == "/auth/session" && req.Method == "GET") return AuthSession(req);

            Session session = plugin.Sessions.Get(req.Cookie(SessionManager.SessionCookie));
            if (session == null) return HttpResponse.Error(401, "unauthenticated", "Session expirée : rechargez la tablette.");
            PlayerSnapshot player = plugin.Players.Get(session.SteamId);
            TabletProfile profile = plugin.Db.GetProfile(session.SteamId, session.CharacterId);

            if (path == "/auth/logout" && req.Method == "POST")
            {
                plugin.Sessions.Revoke(session.Token);
                return HttpResponse.Json(new { ok = true }).AddHeader("Set-Cookie", $"{SessionManager.SessionCookie}=; Path=/; Max-Age=0; HttpOnly; SameSite=Lax");
            }
            if (path == "/lock/unlock" && req.Method == "POST")
                lock (pinSync) return Unlock(req, session, plugin.Db.GetProfile(session.SteamId, session.CharacterId));
            if (path == "/lock/lock" && req.Method == "POST")
            {
                session.Unlocked = false;
                return HttpResponse.Json(LockState(session, profile));
            }

            if (profile.HasPin && !session.Unlocked) return HttpResponse.Error(423, "locked", "La tablette est verrouillée.");

            if (path == "/lock/pin" && req.Method == "POST")
                lock (pinSync) return ChangePin(req, session, plugin.Db.GetProfile(session.SteamId, session.CharacterId));
            if (path == "/me" && req.Method == "GET") return HttpResponse.Json(Me(player));
            if (path == "/apps" && req.Method == "GET") return HttpResponse.Json(AppList(player, profile));
            if (path == "/settings" && req.Method == "GET") return HttpResponse.Json(SettingsOf(profile));
            if (path == "/settings" && req.Method == "POST") return UpdateSettings(req, profile);
            if (path == "/settings/reset" && req.Method == "POST") return ResetSettings(profile);
            if (path.StartsWith("/storage/")) return Storage(req, session, player, path.Substring(9));

            HttpResponse feature = plugin.Features?.Handle(new ApiContext { Req = req, Path = path, Session = session, Player = player, Profile = profile });
            return feature ?? HttpResponse.Error(404, "not_found");
        }

        private HttpResponse Info()
        {
            string serverName = null;
            try { serverName = Nova.RemoveTextFormat(Nova.serverInfo?.serverName ?? ""); } catch { }
            return HttpResponse.Json(new
            {
                name = Config.TabletName,
                version = MyIntranetPlugin.Version,
                server = serverName,
                command = Config.ChatCommand
            });
        }

        private HttpResponse AuthSession(HttpRequest req)
        {
            Session session = plugin.Sessions.Get(req.Cookie(SessionManager.SessionCookie));
            HttpResponse withCookie = null;

            if (session == null)
            {
                // Code validé en jeu ?
                session = plugin.Sessions.ConsumePairing(req.Cookie(SessionManager.PairingCookie));

                // Identification automatique : un seul joueur connecté depuis cette adresse IP
                if (session == null && Config.AutoLoginByIp && req.RemoteIp != null)
                {
                    List<PlayerSnapshot> candidates = plugin.Players.FindByIp(req.RemoteIp);
                    if (candidates.Count == 1) session = plugin.Sessions.Create(candidates[0]);
                }

                if (session == null)
                {
                    if (!Config.PairingCodeEnabled)
                        return HttpResponse.Json(new { status = "unknown", message = "Connectez-vous au serveur de jeu puis rechargez la tablette." });

                    Pairing pairing = plugin.Sessions.GetOrCreatePairing(req.Cookie(SessionManager.PairingCookie));
                    return HttpResponse.Json(new
                    {
                        status = "pairing",
                        code = pairing.Code,
                        command = $"{Config.ChatCommand} {pairing.Code}",
                        expiresIn = pairing.ExpiresAt - Clock.Now
                    }).AddHeader("Set-Cookie", $"{SessionManager.PairingCookie}={pairing.Token}; Path=/; Max-Age={Config.PairingCodeLifetimeSeconds}; HttpOnly; SameSite=Lax");
                }

                withCookie = new HttpResponse();
                withCookie.AddHeader("Set-Cookie", $"{SessionManager.SessionCookie}={session.Token}; Path=/; Max-Age={Config.SessionLifetimeHours * 3600}; HttpOnly; SameSite=Lax");
                withCookie.AddHeader("Set-Cookie", $"{SessionManager.PairingCookie}=; Path=/; Max-Age=0; HttpOnly; SameSite=Lax");
            }

            PlayerSnapshot player = plugin.Players.Get(session.SteamId);
            TabletProfile profile = plugin.Db.GetProfile(session.SteamId, session.CharacterId);
            HttpResponse response = HttpResponse.Json(new
            {
                status = "ok",
                player = new { name = player.FullName, firstName = player.FirstName },
                @lock = LockState(session, profile),
                // Apparence visible dès l'écran de verrouillage
                appearance = new
                {
                    theme = profile.Theme,
                    accent = profile.Accent,
                    wallpaper = profile.Wallpaper,
                    brightness = profile.Brightness,
                    textScale = profile.TextScale,
                    use24h = profile.Use24h
                }
            });
            if (withCookie != null) response.Headers.AddRange(withCookie.Headers);
            return response;
        }

        private object LockState(Session session, TabletProfile profile) => new
        {
            hasPin = profile.HasPin,
            unlocked = !profile.HasPin || session.Unlocked,
            lockedFor = Math.Max(0, profile.LockedUntil - Clock.Now),
            autoLockMinutes = profile.AutoLockMinutes
        };

        private HttpResponse Unlock(HttpRequest req, Session session, TabletProfile profile)
        {
            if (!profile.HasPin)
            {
                session.Unlocked = true;
                return HttpResponse.Json(LockState(session, profile));
            }
            if (profile.LockedUntil > Clock.Now)
                return HttpResponse.Json(new { error = "too_many_attempts", lockedFor = profile.LockedUntil - Clock.Now }, 429);
            if (IpThrottled(req.RemoteIp))
                return HttpResponse.Json(new { error = "too_many_attempts", lockedFor = 60 }, 429);

            string pin = ReadJson(req)?["pin"]?.ToString();
            bool ok = PinSecurity.Check(profile, pin, Config);
            plugin.Db.Save(profile);
            if (!ok)
            {
                long lockedFor = Math.Max(0, profile.LockedUntil - Clock.Now);
                return HttpResponse.Json(new
                {
                    error = lockedFor > 0 ? "too_many_attempts" : "wrong_pin",
                    lockedFor,
                    attemptsLeft = Config.PinMaxAttempts - profile.FailedAttempts
                }, lockedFor > 0 ? 429 : 403);
            }
            session.Unlocked = true;
            return HttpResponse.Json(LockState(session, profile));
        }

        /// <summary>Limite globale par IP (20 essais/minute), en plus du blocage par compte.</summary>
        private bool IpThrottled(string ip)
        {
            Queue<long> attempts = pinAttemptsByIp.GetOrAdd(ip ?? "?", _ => new Queue<long>());
            lock (attempts)
            {
                long now = Clock.Now;
                while (attempts.Count > 0 && attempts.Peek() < now - 60) attempts.Dequeue();
                if (attempts.Count >= 20) return true;
                attempts.Enqueue(now);
                return false;
            }
        }

        private HttpResponse ChangePin(HttpRequest req, Session session, TabletProfile profile)
        {
            JObject body = ReadJson(req);
            string current = body?["current"]?.ToString();
            string pin = body?["pin"]?.ToString() ?? "";

            if (pin.Length > 0 && !PinSecurity.IsValidPin(pin))
                return HttpResponse.Error(400, "invalid_pin", "Le code doit contenir exactement 4 chiffres.");

            if (profile.HasPin)
            {
                if (profile.LockedUntil > Clock.Now)
                    return HttpResponse.Json(new { error = "too_many_attempts", lockedFor = profile.LockedUntil - Clock.Now }, 429);
                bool ok = PinSecurity.Check(profile, current, Config);
                if (!ok)
                {
                    plugin.Db.Save(profile);
                    return HttpResponse.Error(403, "wrong_pin", "Code actuel incorrect.");
                }
            }

            PinSecurity.SetPin(profile, pin);
            plugin.Db.Save(profile);
            session.Unlocked = true;
            return HttpResponse.Json(new { ok = true, hasPin = profile.HasPin });
        }

        private object Me(PlayerSnapshot p) => new
        {
            steamId = p.SteamId.ToString(),
            steamName = p.SteamName,
            characterId = p.CharacterId,
            firstName = p.FirstName,
            lastName = p.LastName,
            fullName = p.FullName,
            phoneNumber = p.PhoneNumber,
            birthday = p.Birthday,
            level = p.Level,
            xp = p.Xp,
            money = Config.ShowMoney ? p.Money : (double?)null,
            bank = Config.ShowMoney ? p.Bank : (double?)null,
            biz = p.BizId > 0 ? new { id = p.BizId, name = p.BizName } : null,
            isAdmin = p.IsAdmin,
            updatedAt = p.UpdatedAt
        };

        private object AppList(PlayerSnapshot player, TabletProfile profile)
        {
            List<string> order = ParseList(profile.AppOrder);
            HashSet<string> hidden = new HashSet<string>(ParseList(profile.HiddenApps));
            return plugin.Apps.All
                .Where(a => !a.AdminOnly || player.IsAdmin)
                .OrderBy(a => order.IndexOf(a.Id) < 0 ? int.MaxValue : order.IndexOf(a.Id))
                .Select(a => new
                {
                    id = a.Id,
                    name = a.Name,
                    description = a.Description,
                    version = a.Version,
                    author = a.Author,
                    color = a.Color,
                    icon = AppRegistry.IconOf(a),
                    url = $"/apps/{a.Id}/{a.Entry}",
                    builtin = a.Builtin,
                    adminOnly = a.AdminOnly,
                    hidden = !a.Builtin && hidden.Contains(a.Id)
                })
                .ToList();
        }

        private static object SettingsOf(TabletProfile p) => new
        {
            theme = p.Theme,
            accent = p.Accent,
            wallpaper = p.Wallpaper,
            brightness = p.Brightness,
            textScale = p.TextScale,
            use24h = p.Use24h,
            autoLockMinutes = p.AutoLockMinutes,
            appOrder = ParseList(p.AppOrder),
            hiddenApps = ParseList(p.HiddenApps),
            dock = ParseList(p.Dock),
            widgets = ParseList(p.Widgets),
            hasPin = p.HasPin
        };

        private HttpResponse UpdateSettings(HttpRequest req, TabletProfile p)
        {
            JObject patch = ReadJson(req);
            if (patch == null) return HttpResponse.Error(400, "invalid_json");

            try
            {
                foreach (JProperty prop in patch.Properties())
                {
                    JToken v = prop.Value;
                    switch (prop.Name)
                    {
                        case "theme":
                            p.Theme = OneOf((string)v, Themes, "theme");
                            break;
                        case "accent":
                            p.Accent = Match((string)v, AccentPattern, "accent").ToLowerInvariant();
                            break;
                        case "wallpaper":
                            string wallpaper = (string)v ?? "";
                            p.Wallpaper = WallpaperPresets.Contains(wallpaper) ? wallpaper
                                : "url:" + Match(wallpaper.StartsWith("url:") ? wallpaper.Substring(4) : wallpaper, WallpaperUrlPattern, "wallpaper");
                            break;
                        case "brightness":
                            p.Brightness = Clamp((int)v, 20, 100);
                            break;
                        case "textScale":
                            p.TextScale = Clamp((int)v, 80, 140);
                            break;
                        case "use24h":
                            p.Use24h = (bool)v;
                            break;
                        case "autoLockMinutes":
                            p.AutoLockMinutes = Clamp((int)v, 0, 60);
                            break;
                        case "appOrder":
                            p.AppOrder = AppIds(v, 200);
                            break;
                        case "hiddenApps":
                            p.HiddenApps = AppIds(v, 200, AppRegistry.SettingsAppId);
                            break;
                        case "dock":
                            p.Dock = AppIds(v, 5);
                            break;
                        case "widgets":
                            List<string> widgets = v.ToObject<List<string>>() ?? new List<string>();
                            p.Widgets = JsonConvert.SerializeObject(widgets.Where(WidgetIds.Contains).Distinct().ToList());
                            break;
                        default:
                            throw new ArgumentException($"Réglage inconnu : {prop.Name}");
                    }
                }
            }
            catch (Exception e) when (e is ArgumentException || e is FormatException || e is InvalidCastException || e is JsonException)
            {
                return HttpResponse.Error(400, "invalid_settings", e.Message);
            }

            plugin.Db.Save(p);
            return HttpResponse.Json(SettingsOf(p));
        }

        private HttpResponse ResetSettings(TabletProfile p)
        {
            TabletProfile defaults = new TabletProfile();
            p.Theme = defaults.Theme;
            p.Accent = defaults.Accent;
            p.Wallpaper = defaults.Wallpaper;
            p.Brightness = defaults.Brightness;
            p.TextScale = defaults.TextScale;
            p.Use24h = defaults.Use24h;
            p.AutoLockMinutes = defaults.AutoLockMinutes;
            p.AppOrder = defaults.AppOrder;
            p.HiddenApps = defaults.HiddenApps;
            p.Dock = defaults.Dock;
            p.Widgets = defaults.Widgets;
            plugin.Db.Save(p);
            return HttpResponse.Json(SettingsOf(p));
        }

        /// <summary>Stockage clé/valeur par joueur et par application : /api/storage/{appId}[/{clé}]</summary>
        private HttpResponse Storage(HttpRequest req, Session session, PlayerSnapshot player, string rest)
        {
            string[] parts = rest.Split('/');
            AppManifest app = plugin.Apps.Find(parts[0]);
            if (app == null || (app.AdminOnly && !player.IsAdmin)) return HttpResponse.Error(404, "unknown_app");
            string owner = session.OwnerKey;

            if (parts.Length == 1)
            {
                if (req.Method != "GET") return HttpResponse.Error(405, "method_not_allowed");
                return HttpResponse.Json(plugin.Db.StorageList(owner, app.Id).Select(e => new { key = e.Key, updatedAt = e.UpdatedAt }));
            }

            string key = parts.Length == 2 ? parts[1] : null;
            if (key == null || !StorageKeyPattern.IsMatch(key)) return HttpResponse.Error(400, "invalid_key", "Clé : 1 à 64 caractères parmi A-Z a-z 0-9 _ . -");

            switch (req.Method)
            {
                case "GET":
                    AppStorageEntry entry = plugin.Db.StorageGet(owner, app.Id, key);
                    if (entry == null) return HttpResponse.Error(404, "not_found");
                    return HttpResponse.Json(new { key, value = JToken.Parse(entry.Value), updatedAt = entry.UpdatedAt });

                case "POST":
                case "PUT":
                    if (req.Body.Length > MaxStorageValueBytes) return HttpResponse.Error(413, "too_large", "Valeur limitée à 64 Ko.");
                    JObject body = ReadJson(req);
                    if (body == null || body["value"] == null) return HttpResponse.Error(400, "invalid_json", "Corps attendu : { \"value\": ... }");
                    if (plugin.Db.StorageGet(owner, app.Id, key) == null && plugin.Db.StorageList(owner, app.Id).Count >= MaxStorageKeysPerApp)
                        return HttpResponse.Error(413, "too_many_keys", $"Limite de {MaxStorageKeysPerApp} clés par application.");
                    plugin.Db.StorageSet(owner, app.Id, key, body["value"].ToString(Formatting.None));
                    return HttpResponse.Json(new { ok = true });

                case "DELETE":
                    return HttpResponse.Json(new { ok = plugin.Db.StorageDelete(owner, app.Id, key) });
            }
            return HttpResponse.Error(405, "method_not_allowed");
        }

        // ------------------------------------------------------------------
        //  Utilitaires
        // ------------------------------------------------------------------

        private static JObject ReadJson(HttpRequest req)
        {
            try { return req.Body.Length == 0 ? new JObject() : JObject.Parse(req.BodyText); }
            catch { return null; }
        }

        private static List<string> ParseList(string json)
        {
            try { return JsonConvert.DeserializeObject<List<string>>(json ?? "[]") ?? new List<string>(); }
            catch { return new List<string>(); }
        }

        private static string AppIds(JToken value, int max, string excluded = null)
        {
            List<string> ids = (value.ToObject<List<string>>() ?? new List<string>())
                .Where(id => id != null && Regex.IsMatch(id, "^[a-z0-9][a-z0-9_-]{1,31}$") && id != excluded)
                .Distinct()
                .Take(max)
                .ToList();
            return JsonConvert.SerializeObject(ids);
        }

        private static string OneOf(string value, string[] allowed, string name)
        {
            if (value == null || !allowed.Contains(value)) throw new ArgumentException($"Valeur invalide pour {name}.");
            return value;
        }

        private static string Match(string value, Regex pattern, string name)
        {
            if (value == null || !pattern.IsMatch(value)) throw new ArgumentException($"Valeur invalide pour {name}.");
            return value;
        }

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    }
}
