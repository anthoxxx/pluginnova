using System;
using System.Collections;
using System.IO;
using Life;
using Life.DB;
using Life.Network;
using Mirror;
using Newtonsoft.Json;
using UnityEngine;

namespace MusiqueConnexion
{
    /// <summary>
    /// Joue un son à chaque joueur quand il a fini de charger et apparaît en jeu.
    /// </summary>
    public class MusiqueConnexionPlugin : Plugin
    {
        // Les sons du jeu sont les mêmes que ceux de "ticketAlertSound" (0 à 3)
        private const int IndexMax = 3;

        private Config config;
        private string configPath;

        public MusiqueConnexionPlugin(IGameAPI api) : base(api) { }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            configPath = Path.Combine(pluginsPath, "MusiqueConnexion", "config.json");
            LoadConfig();
            RegisterCommands();

            Debug.Log("[MusiqueConnexion] initialisé");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            if (!config.Active || player.setup == null) return;

            // CharacterSetup est un MonoBehaviour : la coroutine s'exécute sur le thread Unity
            player.setup.StartCoroutine(PlayWhenLoaded(player));
        }

        private IEnumerator PlayWhenLoaded(Player player)
        {
            float timeout = Time.time + 120f;
            yield return new WaitUntil(() => player.setup == null || player.finishedLoading || Time.time > timeout);
            if (config.DelaiSecondes > 0) yield return new WaitForSeconds(config.DelaiSecondes);
            if (player.setup == null) yield break;

            Play(player, config.IndexSon, config.Volume);
        }

        private static void Play(Player player, int index, float volume)
        {
            player.setup.TargetPlayClaironById(volume, index);
        }

        private void RegisterCommands()
        {
            new SChatCommand("/musiqueconnexion", new string[] { "/mc" },
                "Tester ou recharger le son de connexion (admin)",
                "/musiqueconnexion <0-3> [volume 0-1] | reload",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!player.IsAdmin)
                    {
                        player.Notify("MusiqueConnexion", "Commande réservée aux administrateurs.", NotificationManager.Type.Error);
                        return;
                    }

                    if (args.Length > 0 && args[0].Equals("reload", StringComparison.OrdinalIgnoreCase))
                    {
                        LoadConfig();
                        player.Notify("MusiqueConnexion", $"Configuration rechargée (son {config.IndexSon}, volume {config.Volume}).", NotificationManager.Type.Success);
                        return;
                    }

                    int index = config.IndexSon;
                    float volume = config.Volume;
                    if (args.Length > 0 && (!int.TryParse(args[0], out index) || index < 0 || index > IndexMax))
                    {
                        player.Notify("MusiqueConnexion", $"Numéro de son invalide (0 à {IndexMax}).", NotificationManager.Type.Warning);
                        return;
                    }
                    if (args.Length > 1 && float.TryParse(args[1].Replace(',', '.'), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float v))
                        volume = Mathf.Clamp01(v);

                    Play(player, index, volume);
                    player.Notify("MusiqueConnexion", $"Son {index} joué (volume {volume}).", NotificationManager.Type.Info);
                })).Register();
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(configPath))
                    config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(configPath)) ?? new Config();
                else
                    config = new Config();

                config.IndexSon = Mathf.Clamp(config.IndexSon, 0, IndexMax);
                config.Volume = Mathf.Clamp01(config.Volume);
                config.DelaiSecondes = Mathf.Max(0f, config.DelaiSecondes);

                // Réécrit le fichier pour créer les champs manquants
                Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                File.WriteAllText(configPath, JsonConvert.SerializeObject(config, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogError($"[MusiqueConnexion] config.json illisible, valeurs par défaut utilisées : {e.Message}");
                config = new Config();
            }
        }
    }

    public class Config
    {
        public bool Active = true;
        /// <summary>Son du jeu à jouer (0 à 3, mêmes sons que ticketAlertSound)</summary>
        public int IndexSon = 0;
        /// <summary>Volume entre 0 et 1</summary>
        public float Volume = 0.8f;
        /// <summary>Attente après la fin du chargement, en secondes</summary>
        public float DelaiSecondes = 2f;
    }
}
