using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Life;
using Life.AreaSystem;
using Life.Network;
using Life.UI;
using Newtonsoft.Json;
using UnityEngine;

namespace SongByLorisStrange
{
    /// <summary>
    /// Song By Loris Strange : diffuse un lien (YouTube ou autre) sur les écrans web
    /// du jeu (<see cref="WebScreenRP"/>). L'URL est enregistrée dans les données de
    /// l'objet : le jeu la synchronise à tous les joueurs, qui voient et entendent
    /// la même chose.
    /// </summary>
    public class SongPlugin : Plugin
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

        public SongPlugin(IGameAPI api) : base(api) { }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            dataPath = Path.Combine(pluginsPath, "SongByLorisStrange");
            Directory.CreateDirectory(dataPath);
            LoadConfig();
            LoadHistory();

            RegisterCommands();

            Debug.Log($"[{Title}] chargé");
        }

        // ------------------------------------------------------------------
        //  Commandes
        // ------------------------------------------------------------------

        private void RegisterCommands()
        {
            new SChatCommand("/song", new[] { "/musique" }, "Mettre une musique sur l'écran le plus proche", "/song",
                (Action<Player, string[]>)((player, args) =>
                {
                    WebScreenRP screen = GetClosestScreen(player);
                    if (screen == null)
                    {
                        player.Notify(Title, $"Approche-toi d'un écran web (moins de {config.MaxDistance} m).", NotificationManager.Type.Error, 5f);
                        return;
                    }
                    if (!CanControl(player, screen))
                    {
                        player.Notify(Title, "Seuls le propriétaire du terrain, ses co-propriétaires et le staff peuvent changer la musique.", NotificationManager.Type.Error, 5f);
                        return;
                    }

                    // "/song <lien>" lance directement le lien
                    if (args.Length > 0)
                        Play(player, screen, string.Join(" ", args));
                    else
                        OpenMenu(player, screen);
                })).Register();

            new SChatCommand("/songstop", "Couper la musique de tous les écrans (staff)", "/songstop",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!player.IsAdminService)
                    {
                        player.Notify(Title, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
                        return;
                    }

                    int count = 0;
                    foreach (WebScreenRP screen in GetAllScreens().Where(IsPlaying))
                    {
                        SetUrl(screen, BlankUrl);
                        count++;
                    }
                    player.Notify(Title, $"{count} écran(s) coupé(s).", NotificationManager.Type.Success, 5f);
                })).Register();
        }

        // ------------------------------------------------------------------
        //  Menus
        // ------------------------------------------------------------------

        private void OpenMenu(Player player, WebScreenRP screen)
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

        private void AskLink(Player player, WebScreenRP screen)
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

        private void OpenHistory(Player player, WebScreenRP screen)
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

        private bool Play(Player player, WebScreenRP screen, string link)
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

            foreach (Player listener in Nova.server.GetPlayersInRange(config.AnnounceRange, screen.transform.position))
                listener.Notify(Title, $"{player.FullName} a lancé une musique.", NotificationManager.Type.Info, 5f);

            Debug.Log($"[{Title}] {player.FullName} joue {link} (terrain {screen.areaId}, objet {screen.objectId})");
            return true;
        }

        private void Stop(Player player, WebScreenRP screen)
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
        /// Même mécanisme que le jeu pour les panneaux : modifier les données de l'objet
        /// les sauvegarde et les envoie à tous les joueurs (RpcSetObject).
        /// </summary>
        private static void SetUrl(WebScreenRP screen, string url)
        {
            screen.SetData(JsonConvert.SerializeObject(new WebScreenData { url = url }));
        }

        private static string GetUrl(WebScreenRP screen)
        {
            if (string.IsNullOrEmpty(screen.data)) return null;
            try
            {
                return JsonConvert.DeserializeObject<WebScreenData>(screen.data)?.url;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static bool IsPlaying(WebScreenRP screen)
        {
            string url = GetUrl(screen);
            return !string.IsNullOrEmpty(url) && url != BlankUrl;
        }

        private static IEnumerable<WebScreenRP> GetAllScreens()
        {
            foreach (LifeArea area in Nova.a.areas)
            {
                Dictionary<int, AreaObject> objects = area?.instance?.localObjects;
                if (objects == null) continue;

                foreach (AreaObject obj in objects.Values)
                {
                    if (obj == null) continue;
                    WebScreenRP screen = obj.GetComponent<WebScreenRP>();
                    if (screen != null) yield return screen;
                }
            }
        }

        private WebScreenRP GetClosestScreen(Player player)
        {
            Vector3 position = player.setup.transform.position;
            WebScreenRP closest = null;
            float best = config.MaxDistance;

            foreach (WebScreenRP screen in GetAllScreens())
            {
                float distance = Vector3.Distance(position, screen.transform.position);
                if (distance <= best)
                {
                    best = distance;
                    closest = screen;
                }
            }
            return closest;
        }

        private bool CanControl(Player player, WebScreenRP screen)
        {
            if (player.IsAdminService || !config.OwnerOnly) return true;

            LifeArea area = Nova.a.GetAreaById(screen.areaId);
            return area?.permissions != null && area.permissions.HasPermission(player.character.Id);
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
    }

    public class SongEntry
    {
        public string Link;
        public string By;
        public DateTime Date;
    }
}
