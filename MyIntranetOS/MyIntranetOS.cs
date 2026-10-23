// ============================================================================
//  MyIntranet OS - Plugin Nova Life (tablette intranet reliée au jeu)
//  By Matheo Mercier & Loris Strange
//  - Serveur web autonome (HttpListener) : sert Web/ et Application(s)/
//  - API : /api/me /api/apps /api/settings /api/pin/verify /api/pin/set
//  - Sauvegarde JSON temps réel : Data/mios.json (écriture atomique + .bak)
//  - Identification : cookie de session (/tablette) OU IP du joueur en jeu
//  Tout ce qui touche au jeu se fait sur le thread principal (évènements Nova) :
//  le serveur web ne lit qu'une copie (snapshot) des joueurs connectés.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Life;                // Nova.server, IGameAPI
using Life.DB;             // Characters
using Life.Network;        // Player, SChatCommand
using Mirror;              // NetworkConnection
using ModKit.Helper;       // AssemblyHelper
using ModKit.Interfaces;   // PluginInformations
using Debug = UnityEngine.Debug;

namespace MyIntranetOS
{
    public class MyIntranetPlugin : ModKit.ModKit
    {
        public MyIntranetPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.1.1", "By Matheo Mercier & Loris Strange");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            try
            {
                Paths.Init(pluginsPath); Cfg.Load(); Store.Load();
                Nova.server.OnMinutePassedEvent += Bridge.Refresh;   // thread principal
                HttpServer.Start();
                RegisterCommand();
                Log.Info("By Matheo Mercier & Loris Strange - Initialisé - port " + Cfg.C.port + " - données : " + Paths.DataFile);
                if (!string.IsNullOrEmpty(Cfg.C.devSteamId))
                    Log.Err("devSteamId est défini : toute personne non identifiée sera vue comme " + Cfg.C.devSteamId + ". À vider en production !");
            }
            catch (Exception e) { Log.Err("Init : " + e); }
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            Bridge.Track(player, conn);
        }

        public override void OnPlayerDisconnect(NetworkConnection conn)
        {
            base.OnPlayerDisconnect(conn);
            Bridge.Untrack(conn);
        }

        void RegisterCommand()
        {
            try
            {   // /tablette : donne au joueur son lien personnel (cookie de session)
                new SChatCommand("/tablette", "Ouvre votre tablette", "/tablette", (Action<Player, string[]>)((player, args) =>
                {
                    Bridge.Track(player, null);
                    string tok = Sessions.Create(player.steamId.ToString());
                    player.SendText("<color=#0a84ff>Tablette :</color> " + Cfg.C.publicUrl.TrimEnd('/') + "/?t=" + tok);
                })).Register();
            }
            catch (Exception e) { Log.Err("Commande /tablette : " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- API publique pour vos autres plugins
    public static class MyIntranetAPI
    {
        /// <summary>Envoie une notification dans la tablette du joueur (sauvegardée en temps réel).</summary>
        public static void Notify(ulong steamId, string title, string desc)
        {
            Store.Mutate(db =>
            {
                var p = Store.Player(db, steamId.ToString());
                p.notifs.Insert(0, new Notif { t = title, d = desc, ts = Now() });
                if (p.notifs.Count > 30) p.notifs.RemoveRange(30, p.notifs.Count - 30);
            });
        }
        /// <summary>Publie une actualité visible par tous (widget Actualités).</summary>
        public static void PublishNews(string tag, string title, string desc)
        {
            Store.Mutate(db =>
            {
                db.news.Insert(0, new NewsItem { tag = tag, t = title, d = desc });
                if (db.news.Count > 10) db.news.RemoveRange(10, db.news.Count - 10);
            });
        }
        internal static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }
    }

    static class Log
    {
        public static void Info(string m) { Debug.Log("[MyIntranetOS] " + m); }
        public static void Err(string m) { Debug.LogError("[MyIntranetOS][ERREUR] " + m); }
    }

    // ---------------------------------------------------------------- chemins
    static class Paths
    {
        public static string Root, Web, Apps, Conf, Data, DataFile, ConfFile;
        public static void Init(string pluginsPath)
        {
            // Assembly.Location est vide quand le jeu charge la DLL en mémoire : on part de pluginsPath
            string dll = pluginsPath;
            if (!string.IsNullOrEmpty(dll) && File.Exists(dll)) dll = Path.GetDirectoryName(dll);
            if (string.IsNullOrEmpty(dll) || !Directory.Exists(dll))
            {
                string loc = Assembly.GetExecutingAssembly().Location;
                dll = string.IsNullOrEmpty(loc) ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(loc);
            }
            Root = Path.Combine(dll, "MyIntranetOS");
            Web = Path.Combine(Root, "Web"); Apps = Path.Combine(Root, "Application(s)");
            Conf = Path.Combine(Root, "Configuration"); Data = Path.Combine(Root, "Data");
            foreach (var d in new[] { Root, Web, Apps, Conf, Data }) Directory.CreateDirectory(d);
            DataFile = Path.Combine(Data, "mios.json"); ConfFile = Path.Combine(Conf, "configuration.json");
        }
    }

    // ---------------------------------------------------------------- configuration.json
    class CfgModel
    {
        public int port = 8080;
        public string publicUrl = "http://127.0.0.1:8080";   // adresse que les joueurs peuvent atteindre
        public string city = "Amboise";
        public bool identifyByIp = true;                      // identifie le joueur par l'IP de sa session de jeu
        public string devSteamId = "";                        // test hors jeu : SteamID à simuler
        public int sessionHours = 12;
        public int pinMaxFails = 3;
        public int pinLockSeconds = 30;
    }
    static class Cfg
    {
        public static CfgModel C = new CfgModel();
        public static void Load()
        {
            if (File.Exists(Paths.ConfFile))
                try { C = JsonConvert.DeserializeObject<CfgModel>(File.ReadAllText(Paths.ConfFile)) ?? new CfgModel(); }
                catch (Exception e) { Log.Err("configuration.json invalide : " + e.Message); }
            File.WriteAllText(Paths.ConfFile, JsonConvert.SerializeObject(C, Formatting.Indented));
        }
    }

    // ---------------------------------------------------------------- données + sauvegarde TEMPS RÉEL (mios.json)
    class Notif { public string t; public string d; public long ts; }
    class NewsItem { public string tag; public string t; public string d; }
    class PlayerData
    {
        public string name = "";
        public JObject settings = new JObject();
        public string pinHash = ""; public string pinSalt = "";
        public int fails; public long lockedUntil;
        public List<Notif> notifs = new List<Notif>();
        public long lastSeen;
    }
    class DB
    {
        public Dictionary<string, PlayerData> players = new Dictionary<string, PlayerData>();
        public List<NewsItem> news = new List<NewsItem>();
    }
    static class Store
    {
        static DB db = new DB();
        static readonly object L = new object();

        public static void Load()
        {
            lock (L)
            {
                try
                {
                    if (File.Exists(Paths.DataFile))
                        db = JsonConvert.DeserializeObject<DB>(File.ReadAllText(Paths.DataFile)) ?? new DB();
                }
                catch (Exception e)
                {   // fichier corrompu : on tente la sauvegarde .bak
                    Log.Err("mios.json illisible (" + e.Message + "), essai du .bak");
                    try { db = JsonConvert.DeserializeObject<DB>(File.ReadAllText(Paths.DataFile + ".bak")) ?? new DB(); }
                    catch { db = new DB(); }
                }
                SaveLocked();
            }
        }

        public static PlayerData Player(DB d, string id)
        {
            PlayerData p;
            if (!d.players.TryGetValue(id, out p)) { p = new PlayerData(); d.players[id] = p; }
            return p;
        }

        /// <summary>Toute modification passe par ici : appliquée puis écrite sur disque immédiatement.</summary>
        public static void Mutate(Action<DB> change)
        {
            lock (L) { change(db); SaveLocked(); }
        }
        public static T Read<T>(Func<DB, T> read) { lock (L) { return read(db); } }
        public static void Touch(string id, string name)
        {
            lock (L)
            {
                var pd = Player(db, id);
                pd.lastSeen = MyIntranetAPI.Now();
                if (pd.name == name) return;   // pas d'écriture disque si le nom n'a pas changé
                pd.name = name; SaveLocked();
            }
        }

        static void SaveLocked()
        {
            try
            {
                string tmp = Paths.DataFile + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(db, Formatting.Indented));
                if (File.Exists(Paths.DataFile)) File.Replace(tmp, Paths.DataFile, Paths.DataFile + ".bak");
                else File.Move(tmp, Paths.DataFile);
            }
            catch (Exception e) { Log.Err("Sauvegarde : " + e.Message); }
        }
    }

    // ---------------------------------------------------------------- sessions (liens /tablette)
    static class Sessions
    {
        static readonly Dictionary<string, KeyValuePair<string, DateTime>> map = new Dictionary<string, KeyValuePair<string, DateTime>>();
        public static string Create(string steamId)
        {
            byte[] b = new byte[24]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            string tok = BitConverter.ToString(b).Replace("-", "").ToLower();
            lock (map) map[tok] = new KeyValuePair<string, DateTime>(steamId, DateTime.UtcNow.AddHours(Cfg.C.sessionHours));
            return tok;
        }
        public static string Resolve(string tok)
        {
            if (string.IsNullOrEmpty(tok)) return null;
            lock (map)
            {
                KeyValuePair<string, DateTime> v;
                if (!map.TryGetValue(tok, out v)) return null;
                if (v.Value < DateTime.UtcNow) { map.Remove(tok); return null; }
                return v.Key;
            }
        }
    }

    // ---------------------------------------------------------------- pont avec le jeu (seul endroit qui touche l'API Nova Life)
    class PInfo { public string Id; public string Name; public string Ip; public double Money; }
    static class Bridge
    {
        // Ces champs ne sont touchés QUE depuis le thread principal du jeu.
        static readonly Dictionary<Player, NetworkConnection> online = new Dictionary<Player, NetworkConnection>();
        // Copie immuable lue par le serveur web (autre thread).
        static volatile List<PInfo> snap = new List<PInfo>();

        public static void Track(Player player, NetworkConnection conn)
        {
            if (player == null) return;
            NetworkConnection known;
            if (conn == null && online.TryGetValue(player, out known)) conn = known;
            online[player] = conn;
            Refresh();
        }

        public static void Untrack(NetworkConnection conn)
        {
            foreach (var p in online.Where(kv => kv.Key == null || kv.Value == conn).Select(kv => kv.Key).ToList()) online.Remove(p);
            Refresh();
        }

        public static void Refresh()
        {
            var list = new List<PInfo>();
            foreach (var kv in online.ToList())
            {
                try
                {
                    Player p = kv.Key;
                    string ip = null;
                    try { var cc = kv.Value as NetworkConnectionToClient; if (cc != null) ip = cc.address; } catch { }
                    var pi = new PInfo { Id = p.steamId.ToString(), Name = p.FullName, Money = p.character.Bank, Ip = Clean(ip) };
                    list.Add(pi);
                    Store.Touch(pi.Id, pi.Name);
                }
                catch (Exception e) { Log.Err("Rafraîchissement joueur : " + e.Message); online.Remove(kv.Key); }
            }
            snap = list;
        }

        public static string Clean(string ip) { return string.IsNullOrEmpty(ip) ? null : ip.Replace("::ffff:", ""); }
        public static PInfo ById(string id) { return snap.FirstOrDefault(p => p.Id == id); }
        public static PInfo ByIp(string ip)
        {   // plusieurs joueurs derrière la même IP : impossible de savoir lequel, on refuse
            var m = snap.Where(p => p.Ip != null && p.Ip == ip).Take(2).ToList();
            return m.Count == 1 ? m[0] : null;
        }
    }

    // ---------------------------------------------------------------- serveur web
    static class HttpServer
    {
        static HttpListener l;
        static readonly Dictionary<string, string> Mime = new Dictionary<string, string> {
            {".html","text/html; charset=utf-8"},{".js","application/javascript"},{".css","text/css"},{".json","application/json"},
            {".png","image/png"},{".jpg","image/jpeg"},{".jpeg","image/jpeg"},{".svg","image/svg+xml"},{".webp","image/webp"},
            {".gif","image/gif"},{".woff2","font/woff2"},{".ico","image/x-icon"},{".txt","text/plain"} };

        public static void Start()
        {
            int port = Cfg.C.port;
            try { l = new HttpListener(); l.Prefixes.Add("http://*:" + port + "/"); l.Start(); }
            catch
            {
                try { l.Close(); } catch { }
                l = new HttpListener(); l.Prefixes.Add("http://localhost:" + port + "/"); l.Start();
                Log.Info("Mode local uniquement (droits insuffisants pour * : sous Windows, lancez  netsh http add urlacl url=http://*:" + port + "/ user=Tout le monde).");
            }
            Task.Run(() => Loop());
        }
        static async Task Loop()
        {
            while (l.IsListening)
            {
                try { var c = await l.GetContextAsync(); var cc = c; var _ = Task.Run(() => Handle(cc)); }
                catch (Exception e) { if (!l.IsListening) break; Log.Err("Boucle HTTP : " + e.Message); await Task.Delay(200); }
            }
        }

        static void Handle(HttpListenerContext c)
        {
            try
            {
                var rq = c.Request; var rs = c.Response; string path = rq.Url.AbsolutePath;
                string t = rq.QueryString["t"];
                if (t != null && Sessions.Resolve(t) != null)
                {   // lien /tablette : pose le cookie puis redirige
                    rs.AppendCookie(new Cookie("mios", t) { Path = "/", HttpOnly = true });
                    rs.Redirect("/"); rs.Close(); return;   // retire le jeton de l'URL
                }
                if (path.StartsWith("/api/")) Api(c, path.Substring(5)); else Static(c, path);
            }
            catch (Exception e)
            {
                Log.Err("Requête : " + e.Message);
                try { c.Response.StatusCode = 500; c.Response.Close(); } catch { }
            }
        }

        // --- identification du joueur
        static string Who(HttpListenerRequest rq)
        {
            var ck = rq.Cookies["mios"];
            if (ck != null) { var id = Sessions.Resolve(ck.Value); if (id != null) return id; }
            if (Cfg.C.identifyByIp)
            {
                var p = Bridge.ByIp(Bridge.Clean(rq.RemoteEndPoint.Address.ToString()));
                if (p != null) return p.Id;
            }
            return string.IsNullOrEmpty(Cfg.C.devSteamId) ? null : Cfg.C.devSteamId;
        }

        static void Send(HttpListenerContext c, object o, int code = 200)
        {
            byte[] b = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(o));
            c.Response.StatusCode = code; c.Response.ContentType = "application/json; charset=utf-8";
            c.Response.Headers["Cache-Control"] = "no-store";
            c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
        }
        const int MaxBody = 16 * 1024;
        static JObject Body(HttpListenerRequest rq)
        {
            if (rq.ContentLength64 > MaxBody) return new JObject();
            using (var r = new StreamReader(rq.InputStream, Encoding.UTF8))
            {
                var buf = new char[MaxBody + 1]; int n = 0, k;
                while (n < buf.Length && (k = r.Read(buf, n, buf.Length - n)) > 0) n += k;
                if (n > MaxBody) return new JObject();
                try { return JObject.Parse(new string(buf, 0, n)); } catch { return new JObject(); }
            }
        }

        // --- API
        static void Api(HttpListenerContext c, string route)
        {
            string id = Who(c.Request);
            if (id == null) { Send(c, new { error = "Joueur non identifié. Tapez /tablette en jeu." }, 401); return; }
            string m = c.Request.HttpMethod;

            if (route == "me" && m == "GET")
            {
                PInfo pi = Bridge.ById(id);
                var res = Store.Read(db =>
                {
                    var pd = db.players.ContainsKey(id) ? db.players[id] : new PlayerData();
                    return new
                    {
                        name = pi != null ? pi.Name : (string.IsNullOrEmpty(pd.name) ? "Joueur" : pd.name),
                        role = "Joueur",
                        city = Cfg.C.city,
                        money = pi != null ? pi.Money : 0,
                        online = pi != null,
                        hasPin = !string.IsNullOrEmpty(pd.pinHash),
                        notifications = pd.notifs.Take(5).ToList(),
                        news = db.news.Take(3).ToList()
                    };
                });
                Send(c, res); return;
            }
            if (route == "apps" && m == "GET") { Send(c, ScanApps()); return; }

            if (route == "settings")
            {
                if (m == "GET") { Send(c, Store.Read(db => db.players.ContainsKey(id) ? db.players[id].settings.DeepClone() : new JObject())); return; }
                if (m == "POST")
                {
                    JObject b = Body(c.Request); b.Remove("pin");
                    Store.Mutate(db => { var pd = Store.Player(db, id); pd.settings.Merge(b, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace }); });
                    Send(c, new { ok = true }); return;
                }
            }
            if (route == "pin/verify" && m == "POST") { Send(c, VerifyPin(id, Str(Body(c.Request)["code"]))); return; }
            if (route == "pin/set" && m == "POST")
            {
                JObject b = Body(c.Request); string code = Str(b["code"]) ?? "", old = Str(b["old"]) ?? "";
                bool has = Store.Read(db => db.players.ContainsKey(id) && !string.IsNullOrEmpty(db.players[id].pinHash));
                if (has) { var v = VerifyPin(id, old); if (!(bool)v["ok"]) { Send(c, v); return; } }
                if (code.Length > 0 && (code.Length != 4 || !code.All(char.IsDigit))) { Send(c, new { ok = false, error = "4 chiffres requis" }, 400); return; }
                Store.Mutate(db =>
                {
                    var pd = Store.Player(db, id);
                    if (code.Length == 0) { pd.pinHash = ""; pd.pinSalt = ""; }
                    else { pd.pinSalt = Guid.NewGuid().ToString("N"); pd.pinHash = Hash(pd.pinSalt, code); }
                    pd.fails = 0; pd.lockedUntil = 0;
                });
                Send(c, new { ok = true }); return;
            }
            if (route == "notifications/clear" && m == "POST")
            { Store.Mutate(db => Store.Player(db, id).notifs.Clear()); Send(c, new { ok = true }); return; }

            Send(c, new { error = "Route inconnue" }, 404);
        }

        static string Str(JToken t) { return t != null && t.Type == JTokenType.String ? (string)t : null; }

        static string Hash(string salt, string code)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(salt + code))).Replace("-", "");
        }

        // vérification côté serveur + anti-brute-force (3 erreurs = blocage 30 s, persistant)
        static JObject VerifyPin(string id, string code)
        {
            var r = new JObject(); long now = MyIntranetAPI.Now(); bool ok = false; long wait = 0;
            Store.Mutate(db =>
            {
                var pd = Store.Player(db, id);
                if (string.IsNullOrEmpty(pd.pinHash)) { ok = true; return; }
                if (pd.lockedUntil > now) { wait = pd.lockedUntil - now; return; }
                if (code != null && Hash(pd.pinSalt, code) == pd.pinHash) { ok = true; pd.fails = 0; return; }
                if (++pd.fails >= Cfg.C.pinMaxFails) { pd.fails = 0; pd.lockedUntil = now + Cfg.C.pinLockSeconds; wait = Cfg.C.pinLockSeconds; }
            });
            r["ok"] = ok; if (wait > 0) r["wait"] = wait; return r;
        }

        // --- applications : un dossier = une app (app.json + page HTML)
        static List<object> ScanApps()
        {
            var list = new List<object>();
            foreach (var dir in Directory.GetDirectories(Paths.Apps))
            {
                string mf = Path.Combine(dir, "app.json"); if (!File.Exists(mf)) continue;
                try
                {
                    JObject j = JObject.Parse(File.ReadAllText(mf)); string folder = Path.GetFileName(dir);
                    list.Add(new
                    {
                        id = (string)j["id"] ?? folder,
                        name = (string)j["name"] ?? folder,
                        icon = (string)j["icon"] ?? "📱",
                        color = (string)j["color"] ?? "#555",
                        dock = j["dock"] != null && j["dock"].Type == JTokenType.Boolean && (bool)j["dock"],
                        path = "/apps/" + folder + "/" + ((string)j["entry"] ?? "index.html")
                    });
                }
                catch (Exception e) { Log.Err("app.json invalide (" + dir + ") : " + e.Message); }
            }
            return list;
        }

        // --- fichiers statiques (protégés contre ../)
        static void Static(HttpListenerContext c, string path)
        {
            string root = Paths.Web, rel = path.TrimStart('/');
            if (rel == "") rel = "index.html";
            if (rel.StartsWith("apps/"))
            {
                root = Paths.Apps; rel = rel.Substring(5);
            }
            string baseDir = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full;
            try { full = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(rel))); } catch { full = ""; }
            if (!full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) { c.Response.StatusCode = 404; c.Response.Close(); return; }
            string mime; if (!Mime.TryGetValue(Path.GetExtension(full).ToLower(), out mime)) mime = "application/octet-stream";
            byte[] b = File.ReadAllBytes(full);
            c.Response.ContentType = mime; c.Response.Headers["Cache-Control"] = "no-cache";
            c.Response.OutputStream.Write(b, 0, b.Length); c.Response.Close();
        }
    }
}
