using System;
using System.IO;
using Life;
using Life.DB;
using Life.Network;
using Life.UI;
using Mirror;
using Newtonsoft.Json;
using UnityEngine;

namespace LoadingByLorisStrange
{
    /// <summary>
    /// Écran de chargement : à chaque connexion d'un joueur, lance la vidéo
    /// définie dans config.json et affiche un écran de bienvenue.
    /// </summary>
    public class LoadingPlugin : Plugin
    {
        private const string Name = "Loading By Loris Strange";

        private string configPath;
        private LoadingConfig config = new LoadingConfig();

        public LoadingPlugin(IGameAPI api) : base(api) { }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            string directory = Path.Combine(pluginsPath, "LoadingByLorisStrange");
            Directory.CreateDirectory(directory);
            configPath = Path.Combine(directory, "config.json");
            LoadConfig();

            RegisterCommands();

            Debug.Log($"[{Name}] chargé (vidéo : {config.VideoUrl})");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            if (config.Enabled)
                ShowLoadingScreen(player);
        }

        private void ShowLoadingScreen(Player player)
        {
            PlayVideo(player, config.VideoUrl);

            UIPanel panel = new UIPanel(config.Title, UIPanel.PanelType.Text)
                .SetText(config.Message);
            if (config.AllowSkip)
                panel.AddButton(config.SkipButtonText, (p) => player.ClosePanel(p));
            player.ShowPanelUI(panel);
        }

        /// <summary>
        /// Lance la vidéo chez le joueur.
        /// À COMPLÉTER : l'appel exact de l'API Nova-Life (1.69) pour lire une vidéo
        /// côté client n'a pas pu être vérifié ; remplacer le contenu de cette méthode
        /// par cet appel. Tout le reste du plugin (config, connexion, commandes) est prêt.
        /// </summary>
        private void PlayVideo(Player player, string videoUrl)
        {
            if (string.IsNullOrWhiteSpace(videoUrl))
                return;
            Debug.Log($"[{Name}] vidéo pour {player.FullName} : {videoUrl}");
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(configPath))
                    config = JsonConvert.DeserializeObject<LoadingConfig>(File.ReadAllText(configPath)) ?? new LoadingConfig();
                // Réécrit le fichier pour y ajouter les options manquantes
                File.WriteAllText(configPath, JsonConvert.SerializeObject(config, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogError($"[{Name}] config.json invalide, valeurs par défaut utilisées : {e.Message}");
                config = new LoadingConfig();
            }
        }

        private void RegisterCommands()
        {
            // /loading reload : recharge config.json ; /loading test : rejoue l'écran
            SChatCommand command = new SChatCommand("/loading", "Gérer l'écran de chargement (staff)", "/loading <reload|test>",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!player.IsAdmin)
                    {
                        player.Notify(Name, "Commande réservée au staff.", NotificationManager.Type.Error, 5f);
                        return;
                    }

                    string action = args.Length > 0 ? args[0].ToLower() : "";
                    if (action == "reload")
                    {
                        LoadConfig();
                        player.Notify(Name, "Configuration rechargée.", NotificationManager.Type.Success, 5f);
                    }
                    else if (action == "test")
                    {
                        ShowLoadingScreen(player);
                    }
                    else
                    {
                        player.Notify(Name, "Utilisation : /loading <reload|test>", NotificationManager.Type.Info, 5f);
                    }
                }));
            command.Register();
        }
    }

    /// <summary>Contenu de config.json (créé automatiquement au premier lancement).</summary>
    public class LoadingConfig
    {
        public bool Enabled = true;
        public string VideoUrl = "https://exemple.com/ma-video.mp4";
        public string Title = "Loading By Loris Strange";
        public string Message = "Bienvenue sur le serveur !";
        public bool AllowSkip = true;
        public string SkipButtonText = "Passer";
    }
}
