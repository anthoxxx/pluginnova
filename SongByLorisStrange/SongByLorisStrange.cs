using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Life;
using Life.AreaSystem;
using Life.InventorySystem;
using Life.Network;
using Life.UI;
using ModKit.Helper;
using ModKit.Interfaces;
using Newtonsoft.Json;
using UnityEngine;

namespace SongByLorisStrange
{
    /// <summary>
    /// Song By Loris Strange : diffuse un lien (YouTube ou autre) sur les écrans web
    /// du jeu (<see cref="WebScreenRP"/>). L'URL est enregistrée dans les données de
    /// l'objet : le jeu la synchronise à tous les joueurs, qui voient et entendent
    /// la même chose. Accessible depuis AAMenu (Proximité / Interactions) ou /song.
    /// </summary>
    public class SongPlugin : ModKit.ModKit
    {
        private const string Title = "Song By Loris Strange";
        private const string BlankUrl = "about:blank";

        private static readonly Regex YoutubeRegex = new Regex(
            @"(?:youtube\.com/(?:watch\?(?:.*&)?v=|embed/|shorts/|live/)|youtu\.be/)([A-Za-z0-9_-]{11})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex StartRegex = new Regex(@"[?&](?:t|start)=(\d+)", RegexOptions.Compiled);

        private string dataPath;
        private SongConfig config;
        private List<SongEntry> history = new List<SongEntry>();

        public SongPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "anthoxxx");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            dataPath = Path.Combine(pluginsPath, "SongByLorisStrange");
            Directory.CreateDirectory(dataPath);
            LoadConfig();
            LoadHistory();

            RegisterCommands();
            RegisterAAMenu();

            Debug.Log($"[{Title}] chargé");
        }

        // ------------------------------------------------------------------
        //  AAMenu
        // ------------------------------------------------------------------

        private void RegisterAAMenu()
        {
            if (AAMenu.AAMenu.menu == null)
            {
                Debug.LogWarning($"[{Title}] AAMenu introuvable : utilisez la commande /song.");
                return;
            }

            // Proximité : une ligne quand le joueur est près d'un écran web ou d'une radio
            List<int> itemIds = ScreenItemIds
                .Concat(FindItemIds<RadioObject>())
                .Concat(config.ExtraItemIds)
                .Distinct()
                .ToList();
            foreach (int itemId in itemIds)
                AAMenu.Menu.AddProximityTabLine(PluginInformations, itemId, Title, ui => OpenFromAAMenu(ui));
            Debug.Log($"[{Title}] AAMenu > Proximité : objets {string.Join(", ", itemIds)}");

            // Interactions : marche partout, prend l'écran le plus proche
            AAMenu.Menu.AddInteractionTabLine(PluginInformations, Title, ui => OpenFromAAMenu(ui));

            // Administration > Plugins : tout couper
            AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, 1, $"{Title} : couper toutes les musiques", ui =>
            {
                Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                if (player == null) return;
                player.ClosePanel(ui);
                StopAll(player);
            });
        }

        private void OpenFromAAMenu(UIPanel ui)
        {
            Player player = PanelHelper.ReturnPlayerFromPanel(ui);
            if (player == null) return;
            player.ClosePanel(ui);
            OpenClosest(player, null);
        }

        /// <summary>Ids des items qui posent un objet du type T (écran web, radio...).</summary>
        private static List<int> FindItemIds<T>() where T : AreaObject
        {
            List<int> ids = new List<int>();
            foreach (Item item in Nova.man.item.items)
            {
                if (item == null || item.models == null) continue;
                if (item.models.Any(m => m != null && m.placeables != null && m.placeables.Any(p => p is T)))
                    ids.Add(item.id);
            }
            return ids;
        }

        // ------------------------------------------------------------------
        //  Commandes
        // ------------------------------------------------------------------

        private void RegisterCommands()
        {
            new SChatCommand("/song", new[] { "/musique" }, "Mettre une musique sur l'écran le plus proche", "/song",
                (Action<Player, string[]>)((player, args) =>
                    OpenClosest(player, args.Length > 0 ? string.Join(" ", args) : null))).Register();

            new SChatCommand("/songdebug", "Lister les objets posés autour de soi", "/songdebug",
                (Action<Player, string[]>)((player, args) => DebugNearbyObjects(player))).Register();

            new SChatCommand("/songstop", "Couper la musique de tous les écrans (staff)", "/songstop",
                (Action<Player, string[]>)((player, args) => StopAll(player))).Register();
        }

        /// <summary>Ouvre le menu de l'écran le plus proche, ou joue directement <paramref name="link"/>.</summary>
        private void OpenClosest(Player player, string link)
        {
            MusicScreen screen = GetClosestScreen(player);
            if (screen == null)
            {
                player.Notify(Title, $"Aucun écran web à moins de {config.MaxDistance} m. Pose un écran web (à côté de ta radio) : c'est lui qui joue la musique.", NotificationManager.Type.Error, 8f);
                return;
            }
            if (!CanControl(player, screen))
            {
                player.Notify(Title, "Seuls le propriétaire du terrain, ses co-propriétaires et le staff peuvent changer la musique.", NotificationManager.Type.Error, 5f);
                return;
            }

            if (link != null)
                Play(player, screen, link);
            else
                OpenMenu(player, screen);
        }

        private void StopAll(Player player)
        {
            if (!player.IsAdminService)
            {
                player.Notify(Title, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
                return;
            }

            int count = 0;
            foreach (MusicScreen screen in GetAllScreens().Where(IsPlaying))
            {
                SetUrl(screen, BlankUrl);
                count++;
            }
            player.Notify(Title, $"{count} écran(s) coupé(s).", NotificationManager.Type.Success, 5f);
        }

        // ------------------------------------------------------------------
        //  Menus
        // ------------------------------------------------------------------

        private void OpenMenu(Player player, MusicScreen screen)
        {
            string current = IsPlaying(screen) ? GetUrl(screen) : "rien";

            UIPanel panel = new UIPanel(Title, UIPanel.PanelType.Tab)
                .AddTabLine($"En cours : {Shorten(current, 45)}", _ => { })
                .AddTabLine("Mettre un lien YouTube", p =>
                {
                    player.ClosePanel(p);
                    AskLink(player, screen);
                })
                .AddTabLine("Arrêter la musique", p =>
                {
                    player.ClosePanel(p);
                    Stop(player, screen);
                })
                .AddTabLine("Historique", p =>
                {
                    player.ClosePanel(p);
                    OpenHistory(player, screen);
                })
                .AddButton("Choisir", p => p.SelectTab())
                .AddButton("Fermer", p => player.ClosePanel(p));
            player.ShowPanelUI(panel);
        }

        private void AskLink(Player player, MusicScreen screen)
        {
            UIPanel panel = new UIPanel($"{Title} - Lien", UIPanel.PanelType.Input)
                .SetText("Colle un lien YouTube (vidéo, short ou live).\nLes autres liens http(s) (webradio, mp3...) sont aussi acceptés.")
                .SetInputPlaceholder("https://www.youtube.com/watch?v=...")
                .AddButton("Jouer", p =>
                {
                    if (Play(player, screen, p.inputText))
                        player.ClosePanel(p);
                })
                .AddButton("Retour", p =>
                {
                    player.ClosePanel(p);
                    OpenMenu(player, screen);
                });
            player.ShowPanelUI(panel);
        }

        private void OpenHistory(Player player, MusicScreen screen)
        {
            UIPanel panel = new UIPanel($"{Title} - Historique", UIPanel.PanelType.Tab);

            if (history.Count == 0)
                panel.AddTabLine("Aucune musique jouée pour l'instant", _ => { });

            // Le plus récent en premier
            foreach (SongEntry entry in Enumerable.Reverse(history))
            {
                panel.AddTabLine($"{Shorten(entry.Link, 40)} ({entry.By})", p =>
                {
                    player.ClosePanel(p);
                    Play(player, screen, entry.Link);
                });
            }

            panel.AddButton("Rejouer", p => p.SelectTab())
                .AddButton("Retour", p =>
                {
                    player.ClosePanel(p);
                    OpenMenu(player, screen);
                });
            player.ShowPanelUI(panel);
        }

        // ------------------------------------------------------------------
        //  Lecture
        // ------------------------------------------------------------------

        private bool Play(Player player, MusicScreen screen, string link)
        {
            link = link?.Trim();
            if (string.IsNullOrEmpty(link) || link.Length > 500 ||
                !(link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                player.Notify(Title, "Lien invalide : il doit commencer par http:// ou https://", NotificationManager.Type.Error, 5f);
                return false;
            }

            // Re-vérifie au moment de jouer : le panel a pu rester ouvert longtemps
            if (screen == null || !CanControl(player, screen))
            {
                player.Notify(Title, "Cet écran n'est plus accessible.", NotificationManager.Type.Error, 5f);
                return false;
            }

            SetUrl(screen, ToScreenUrl(link));
            AddToHistory(link, player.FullName);

            foreach (Player listener in Nova.server.GetPlayersInRange(config.AnnounceRange, screen.Position))
                listener.Notify(Title, $"{player.FullName} a lancé une musique.", NotificationManager.Type.Info, 5f);

            Debug.Log($"[{Title}] {player.FullName} joue {link} (terrain {screen.AreaId}, objet {screen.Key})");
            return true;
        }

        private void Stop(Player player, MusicScreen screen)
        {
            if (screen == null || !CanControl(player, screen)) return;

            SetUrl(screen, BlankUrl);
            player.Notify(Title, "Musique arrêtée.", NotificationManager.Type.Success, 5f);
        }

        /// <summary>Transforme un lien YouTube en lien lisible par l'écran (voir YoutubeUrlFormat).</summary>
        private string ToScreenUrl(string link)
        {
            Match match = YoutubeRegex.Match(link);
            if (!match.Success) return link;

            Match start = StartRegex.Match(link);
            return config.YoutubeUrlFormat
                .Replace("{id}", match.Groups[1].Value)
                .Replace("{start}", start.Success ? start.Groups[1].Value : "0");
        }

        // ------------------------------------------------------------------
        //  Écrans web
        // ------------------------------------------------------------------

        /// <summary>
        /// Même mécanisme que le jeu (AreaObject.SetData) : l'URL est écrite dans les données
        /// de l'objet, qui sont sauvegardées et envoyées à tous les joueurs (RpcSetObject).
        /// </summary>
        private static void SetUrl(MusicScreen screen, string url)
        {
            string json = JsonConvert.SerializeObject(new WebScreenData { url = url });

            NetworkArea instance = Nova.a.GetAreaById(screen.AreaId)?.instance;
            if (instance?.localObjects != null && instance.localObjects.TryGetValue(screen.Key, out AreaObject areaObject) && areaObject != null)
            {
                areaObject.SetData(json);
                return;
            }

            screen.Object.data = json;
            if (instance?.objects != null) instance.objects[screen.Key] = screen.Object;

            // NetworkAreaManager est interne au jeu : appel par réflexion de
            // NetworkAreaManager.instance.RpcSetObject(areaId, key, objet, false)
            Type manager = typeof(AreaObject).Assembly.GetType("Life.AreaSystem.NetworkAreaManager");
            object managerInstance = manager?.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            MethodInfo rpc = manager?.GetMethod("RpcSetObject", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(int), typeof(int), typeof(LifeObject), typeof(bool) }, null);
            if (managerInstance == null || rpc == null)
            {
                Debug.LogError($"[{Title}] RpcSetObject introuvable : l'écran ne sera mis à jour qu'au prochain chargement.");
                return;
            }
            rpc.Invoke(managerInstance, new object[] { (int)screen.AreaId, screen.Key, screen.Object, false });
        }

        private static string GetUrl(MusicScreen screen)
        {
            string data = screen.Object.data;
            if (string.IsNullOrEmpty(data)) return null;
            try
            {
                return JsonConvert.DeserializeObject<WebScreenData>(data)?.url;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool IsPlaying(MusicScreen screen)
        {
            string url = GetUrl(screen);
            return !string.IsNullOrEmpty(url) && url != BlankUrl;
        }

        /// <summary>Ids des items « écran web ». Calculé au premier besoin (les items doivent être chargés).</summary>
        private HashSet<int> ScreenItemIds
        {
            get
            {
                if (screenItemIds == null || screenItemIds.Count == 0)
                {
                    screenItemIds = new HashSet<int>(FindItemIds<WebScreenRP>().Concat(config.ScreenItemIds));
                    // Secours : écrans déjà instanciés sur le serveur
                    foreach (LifeArea area in Nova.a.areas)
                    {
                        Dictionary<int, AreaObject> local = area?.instance?.localObjects;
                        if (local == null) continue;
                        foreach (AreaObject obj in local.Values.ToList())
                            if (obj != null && obj.GetComponentInChildren<WebScreenRP>(true) != null)
                                screenItemIds.Add(obj.itemId);
                    }
                    Debug.Log($"[{Title}] écrans web : objets {string.Join(", ", screenItemIds)}");
                }
                return screenItemIds;
            }
        }
        private HashSet<int> screenItemIds;

        /// <summary>Tous les écrans web posés, lus dans les données des terrains (comme ModKit).</summary>
        private IEnumerable<MusicScreen> GetAllScreens()
        {
            HashSet<int> itemIds = ScreenItemIds;
            foreach (LifeArea area in Nova.a.areas)
            {
                Dictionary<int, LifeObject> objects = area?.instance?.objects;
                if (objects == null) continue;

                foreach (KeyValuePair<int, LifeObject> entry in objects.ToList())
                {
                    if (entry.Value != null && itemIds.Contains(entry.Value.objectId))
                        yield return new MusicScreen(area.areaId, entry.Key, entry.Value);
                }
            }
        }

        private MusicScreen GetClosestScreen(Player player)
        {
            Vector3 position = player.setup.transform.position;
            MusicScreen closest = null;
            float best = config.MaxDistance;

            foreach (MusicScreen screen in GetAllScreens())
            {
                float distance = Vector3.Distance(position, screen.Position);
                if (distance <= best)
                {
                    best = distance;
                    closest = screen;
                }
            }
            return closest;
        }

        private bool CanControl(Player player, MusicScreen screen)
        {
            if (player.IsAdminService || !config.OwnerOnly) return true;

            LifeArea area = Nova.a.GetAreaById(screen.AreaId);
            return area?.permissions != null && area.permissions.HasPermission(player.character.Id);
        }

        /// <summary>/songdebug : liste les objets posés autour du joueur, pour trouver l'id d'un écran.</summary>
        private void DebugNearbyObjects(Player player)
        {
            Vector3 position = player.setup.transform.position;
            List<string> lines = new List<string>();

            foreach (LifeArea area in Nova.a.areas)
            {
                Dictionary<int, LifeObject> objects = area?.instance?.objects;
                if (objects == null) continue;

                foreach (LifeObject obj in objects.Values.ToList())
                {
                    if (obj == null) continue;
                    float distance = Vector3.Distance(position, new Vector3(obj.x, obj.y, obj.z));
                    if (distance > 10f) continue;

                    Item item = Nova.man.item.GetItem(obj.objectId);
                    string kind = ScreenItemIds.Contains(obj.objectId) ? " [ÉCRAN WEB]" : "";
                    lines.Add($"{distance:0.0} m : objet {obj.objectId} ({(item != null ? item.itemName : "?")}), terrain {area.areaId}{kind}");
                }
            }

            string text = lines.Count == 0
                ? "Aucun objet posé à moins de 10 m."
                : string.Join("\n", lines.Take(15));
            Debug.Log($"[{Title}] /songdebug par {player.FullName} :\n{text}");
            player.ShowPanelUI(new UIPanel($"{Title} - Objets proches", UIPanel.PanelType.Text)
                .SetText($"Écrans web reconnus : {string.Join(", ", ScreenItemIds)}\n\n{text}")
                .AddButton("Fermer", p => player.ClosePanel(p)));
        }

        // ------------------------------------------------------------------
        //  Config et historique (fichiers JSON dans Plugins/SongByLorisStrange/)
        // ------------------------------------------------------------------

        private void LoadConfig()
        {
            string file = Path.Combine(dataPath, "config.json");
            try
            {
                if (File.Exists(file))
                    config = JsonConvert.DeserializeObject<SongConfig>(File.ReadAllText(file));
            }
            catch (Exception e)
            {
                Debug.LogError($"[{Title}] config.json invalide, valeurs par défaut utilisées : {e.Message}");
            }

            config = config ?? new SongConfig();
            // Réécrit le fichier pour y ajouter les nouvelles options éventuelles
            File.WriteAllText(file, JsonConvert.SerializeObject(config, Formatting.Indented));
        }

        private void LoadHistory()
        {
            string file = Path.Combine(dataPath, "history.json");
            try
            {
                if (File.Exists(file))
                    history = JsonConvert.DeserializeObject<List<SongEntry>>(File.ReadAllText(file)) ?? new List<SongEntry>();
            }
            catch (Exception e)
            {
                Debug.LogError($"[{Title}] history.json invalide : {e.Message}");
            }
        }

        private void AddToHistory(string link, string by)
        {
            history.RemoveAll(e => e.Link == link);
            history.Add(new SongEntry { Link = link, By = by, Date = DateTime.Now });
            if (history.Count > config.HistorySize)
                history.RemoveRange(0, history.Count - config.HistorySize);

            try
            {
                File.WriteAllText(Path.Combine(dataPath, "history.json"), JsonConvert.SerializeObject(history, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogError($"[{Title}] impossible d'enregistrer l'historique : {e.Message}");
            }
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text.Substring(0, max - 3) + "...";
        }
    }

    public class SongConfig
    {
        /// <summary>Distance max (m) entre le joueur et l'écran pour /song.</summary>
        public float MaxDistance = 8f;

        /// <summary>Distance (m) des joueurs prévenus quand une musique est lancée.</summary>
        public float AnnounceRange = 30f;

        /// <summary>true : seuls le propriétaire / co-propriétaires du terrain et le staff peuvent changer la musique.</summary>
        public bool OwnerOnly = true;

        /// <summary>Nombre de liens gardés dans l'historique.</summary>
        public int HistorySize = 20;

        /// <summary>
        /// Lien chargé sur l'écran pour une vidéo YouTube. {id} = identifiant de la vidéo,
        /// {start} = seconde de départ. Autre format possible : https://www.youtube.com/watch?v={id}&amp;t={start}s
        /// </summary>
        public string YoutubeUrlFormat = "https://www.youtube.com/embed/{id}?autoplay=1&start={start}&loop=1&playlist={id}&rel=0";

        /// <summary>Ids d'items en plus pour la ligne AAMenu > Proximité (écrans et radios sont détectés seuls).</summary>
        public List<int> ExtraItemIds = new List<int>();

        /// <summary>Ids d'items à traiter comme écrans web si la détection automatique les rate (voir /songdebug).</summary>
        public List<int> ScreenItemIds = new List<int>();
    }

    /// <summary>Un écran web posé : son terrain, sa clé dans le terrain et ses données.</summary>
    public class MusicScreen
    {
        public uint AreaId { get; }
        public int Key { get; }
        public LifeObject Object { get; }
        public Vector3 Position => new Vector3(Object.x, Object.y, Object.z);

        public MusicScreen(uint areaId, int key, LifeObject obj)
        {
            AreaId = areaId;
            Key = key;
            Object = obj;
        }
    }

    public class SongEntry
    {
        public string Link;
        public string By;
        public DateTime Date;
    }
}
