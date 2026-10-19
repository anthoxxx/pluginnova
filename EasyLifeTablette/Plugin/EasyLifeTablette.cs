using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Life;
using Life.DB;
using Life.Network;
using Life.UI;
using Mirror;
using Newtonsoft.Json;
using UnityEngine;

namespace EasyLifeTablette
{
    /// <summary>
    /// Pont entre le serveur Nova-Life et le site de la tablette Easy Life :
    /// - envoie régulièrement les joueurs connectés (nom, argent, banque, staff) au site ;
    /// - affiche en jeu les notifications de la tablette (mails, commandes, Bon Plan...) ;
    /// - commande /tablette : donne un code pour se connecter depuis l'ordinateur (F2) ou un navigateur.
    /// </summary>
    public class EasyLifeTablettePlugin : Plugin
    {
        private static EasyLifeTablettePlugin instance;

        private Config config;
        private readonly Dictionary<NetworkConnection, Player> players = new Dictionary<NetworkConnection, Player>();
        private readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
        private readonly Dictionary<string, DateTime> lastCodeRequest = new Dictionary<string, DateTime>();
        private bool syncRunning;

        public EasyLifeTablettePlugin(IGameAPI api) : base(api) { }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            instance = this;

            // Mono/Unity n'active pas toujours TLS 1.2 par défaut (nécessaire pour un site en https)
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            config = Config.Load(Path.Combine(pluginsPath, "EasyLifeTablette", "config.json"));

            // Objet Unity qui exécute la synchronisation et les retours réseau sur le thread principal
            GameObject runner = new GameObject("EasyLifeTablette");
            UnityEngine.Object.DontDestroyOnLoad(runner);
            runner.AddComponent<Runner>();

            SChatCommand command = new SChatCommand("/tablette", new string[] { "/tab" }, "Obtenir un code de connexion à la tablette Easy Life", "/tablette",
                (Action<Player, string[]>)((player, args) => RequestLoginCode(player)));
            command.Register();

            Debug.Log($"[EasyLifeTablette] chargé, site : {config.ApiUrl}");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            players[conn] = player;
        }

        public override void OnPlayerDisconnect(NetworkConnection conn)
        {
            base.OnPlayerDisconnect(conn);
            players.Remove(conn);
        }

        public override void OnPlayerInput(Player player, KeyCode keyCode, bool state)
        {
            base.OnPlayerInput(player, keyCode, state);
            if (config.CodeKey != KeyCode.None && keyCode == config.CodeKey)
                RequestLoginCode(player);
        }

        // ------------------------------------------------------------------
        //  Connexion à la tablette
        // ------------------------------------------------------------------

        private void RequestLoginCode(Player player)
        {
            // Anti-spam (la touche peut remonter plusieurs évènements d'affilée)
            string id = player.steamId.ToString();
            if (lastCodeRequest.TryGetValue(id, out DateTime last) && (DateTime.UtcNow - last).TotalSeconds < 3) return;
            lastCodeRequest[id] = DateTime.UtcNow;

            string body = JsonConvert.SerializeObject(new { steamId = player.steamId.ToString(), name = player.FullName });

            Post("/api/game/code", body, response =>
            {
                CodeResponse code = JsonConvert.DeserializeObject<CodeResponse>(response);
                UIPanel panel = new UIPanel("Tablette Easy Life", UIPanel.PanelType.Text)
                    .SetText($"Votre code de connexion : <size=32><b>{code.code}</b></size>\n\n" +
                             $"1. Ouvrez l'ordinateur (F2) ou un navigateur\n" +
                             $"2. Allez sur <b>{code.url}</b>\n" +
                             $"3. Saisissez le code (valable 10 minutes)\n\n" +
                             "Hors du jeu, vous pouvez aussi utiliser « Se connecter avec Steam ».")
                    .AddButton("Fermer", p => player.ClosePanel(p));
                player.ShowPanelUI(panel);
            }, error =>
            {
                player.Notify("Tablette", "La tablette est injoignable pour le moment, réessayez plus tard.", NotificationManager.Type.Error, 6f);
            });
        }

        // ------------------------------------------------------------------
        //  Synchronisation avec le site (appelée par Runner sur le thread principal)
        // ------------------------------------------------------------------

        private void Sync()
        {
            if (syncRunning) return;

            List<PlayerData> online = new List<PlayerData>();
            foreach (Player player in players.Values.ToList())
            {
                if (player == null || player.character == null) continue;
                online.Add(new PlayerData
                {
                    steamId = player.steamId.ToString(),
                    name = player.FullName,
                    job = player.HasBiz ? player.biz?.BizName ?? "" : "",
                    money = player.Money,
                    bank = player.Bank,
                    isAdmin = player.IsAdmin,
                });
            }

            string body = JsonConvert.SerializeObject(new
            {
                players = online,
                businesses = config.SyncBusinesses ? ReadBusinesses() : null,
            });

            syncRunning = true;
            Post("/api/game/sync", body, response =>
            {
                syncRunning = false;
                SyncResponse result = JsonConvert.DeserializeObject<SyncResponse>(response);
                foreach (GameNotification n in result?.notifications ?? new List<GameNotification>())
                {
                    Player target = players.Values.FirstOrDefault(p => p != null && p.steamId.ToString() == n.steamId);
                    target?.Notify(n.title, n.body, NotificationManager.Type.Info, 8f);
                }
            }, error =>
            {
                syncRunning = false;
            });
        }

        /// <summary>Entreprises du serveur, pour l'Annuaire de la tablette.</summary>
        private static List<BusinessData> ReadBusinesses()
        {
            if (Nova.biz?.bizs == null) return null;
            return Nova.biz.bizs
                .Where(b => b != null && !string.IsNullOrWhiteSpace(b.BizName))
                .Select(b => new BusinessData { id = b.Id, name = b.BizName, phone = b.Contact ?? "" })
                .ToList();
        }

        /// <summary>
        /// POST JSON vers le site sur un thread séparé (le jeu ne se fige pas),
        /// puis rappel sur le thread principal (obligatoire pour toucher aux joueurs / UI).
        /// </summary>
        private void Post(string path, string json, Action<string> onSuccess, Action<string> onError)
        {
            string url = config.ApiUrl.TrimEnd('/') + path;
            string key = config.ApiKey;

            Task.Run(() =>
            {
                try
                {
                    using (WebClient client = new WebClient())
                    {
                        client.Encoding = Encoding.UTF8;
                        client.Headers[HttpRequestHeader.ContentType] = "application/json";
                        client.Headers["X-Api-Key"] = key;
                        string response = client.UploadString(url, "POST", json);
                        mainThread.Enqueue(() => onSuccess(response));
                    }
                }
                catch (Exception e)
                {
                    string message = e is WebException we && we.Response is HttpWebResponse r ? $"HTTP {(int)r.StatusCode}" : e.Message;
                    mainThread.Enqueue(() =>
                    {
                        Debug.LogWarning($"[EasyLifeTablette] {path} : {message}");
                        onError(message);
                    });
                }
            });
        }

        /// <summary>Composant Unity : vide la file du thread principal et lance la synchro à intervalle régulier.</summary>
        private class Runner : MonoBehaviour
        {
            private float nextSync;

            private void Update()
            {
                EasyLifeTablettePlugin plugin = instance;
                if (plugin == null) return;

                while (plugin.mainThread.TryDequeue(out Action action))
                {
                    try { action(); }
                    catch (Exception e) { Debug.LogError($"[EasyLifeTablette] {e}"); }
                }

                if (Time.unscaledTime >= nextSync)
                {
                    nextSync = Time.unscaledTime + Mathf.Max(10, plugin.config.SyncSeconds);
                    plugin.Sync();
                }
            }
        }
    }

    /// <summary>Plugins/EasyLifeTablette/config.json (créé au premier lancement).</summary>
    internal class Config
    {
        /// <summary>Adresse du site vue depuis le serveur de jeu (ex. http://127.0.0.1:3000 si même machine).</summary>
        public string ApiUrl = "http://127.0.0.1:3000";

        /// <summary>Même valeur que « gameApiKey » dans le config.json du site.</summary>
        public string ApiKey = "REMPLACEZ-PAR-LA-CLE-DU-SITE";

        /// <summary>Intervalle de synchronisation des joueurs (secondes, 10 minimum).</summary>
        public int SyncSeconds = 30;

        /// <summary>Remplir l'Annuaire avec les entreprises du serveur.</summary>
        public bool SyncBusinesses = true;

        /// <summary>Touche qui affiche aussi le code de connexion (« None » = seulement /tablette).</summary>
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public KeyCode CodeKey = KeyCode.None;

        public static Config Load(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, JsonConvert.SerializeObject(new Config(), Formatting.Indented));
                    Debug.LogWarning($"[EasyLifeTablette] config créée : {path}. Renseignez ApiUrl et ApiKey puis redémarrez.");
                }
                Config config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(path)) ?? new Config();
                // Réécrit le fichier pour y ajouter les nouvelles options éventuelles
                File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
                return config;
            }
            catch (Exception e)
            {
                Debug.LogError($"[EasyLifeTablette] config illisible ({path}) : {e.Message}");
                return new Config();
            }
        }
    }

    internal class PlayerData
    {
        public string steamId;
        public string name;
        public string job;
        public double money;
        public double bank;
        public bool isAdmin;
    }

    internal class BusinessData
    {
        public int id;
        public string name;
        public string phone;
    }

    // Remplis par Newtonsoft (désérialisation)
#pragma warning disable 0649
    internal class CodeResponse
    {
        public string code;
        public string url;
    }

    internal class SyncResponse
    {
        public List<GameNotification> notifications;
    }

    internal class GameNotification
    {
        public string steamId;
        public string title;
        public string body;
    }
#pragma warning restore 0649
}
