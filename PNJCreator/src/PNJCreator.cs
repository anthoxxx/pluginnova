using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Life;
using Life.Network;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PNJCreator
{
    /// <summary>
    /// PNJ by Loris Strange — PNJ networkés, apparence sur mesure et IA conversationnelle.
    /// Plugin autonome : uniquement l'API Nova-Life (ni ModKit, ni Harmony).
    /// </summary>
    public class PNJCreator : Plugin
    {
        public const string DisplayName = "PNJ by Loris Strange";
        public const string Version = "1.0.0";
        public const string Credit = "Créé par Loris Strange";

        public DataStore Store;
        public NpcManager Npcs;
        public NpcRunner Runner;
        public Menus Menus;

        readonly Dictionary<string, float> lastSay = new Dictionary<string, float>();
        readonly HashSet<string> pendingSay = new HashSet<string>();

        public PNJCreator(IGameAPI api) : base(api) { }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            Store = new DataStore(Path.Combine(pluginsPath, "PNJCreator"));
            Store.LoadAll();
            Npcs = new NpcManager(Store);
            Menus = new Menus(this);

            var go = new GameObject("PNJCreatorRunner");
            Object.DontDestroyOnLoad(go);
            Runner = go.AddComponent<NpcRunner>();
            Runner.Plugin = this;

            new SChatCommand("/Configpnjcreator", new[] { "/configpnj", "/pnjcreator" }, "Éditeur des PNJ (admin)", "/Configpnjcreator",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!RequireEditor(player)) return;
                    Menus.Main(player);
                })).Register();

            new SChatCommand("/say", "Parler à un PNJ à proximité", "/say <message>",
                (Action<Player, string[]>)((player, args) => Say(player, string.Join(" ", args ?? new string[0])))).Register();

            new SChatCommand("/pnjreload", "Recharge la configuration des PNJ (admin)", "/pnjreload",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!RequireEditor(player)) return;
                    Reload();
                    player.Notify(DisplayName, $"Configuration rechargée : {Store.Config.Npcs.Count} PNJ.", NotificationManager.Type.Success);
                })).Register();

            new SChatCommand("/pnjdebug", "Infos de debug du PNJ le plus proche (admin)", "/pnjdebug",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!RequireEditor(player)) return;
                    var inst = Npcs.Closest(PlayerPosition(player), 15f);
                    if (inst == null)
                    {
                        player.Notify(DisplayName, "Aucun PNJ à moins de 15 m.", NotificationManager.Type.Warning);
                        return;
                    }
                    foreach (string line in Npcs.DebugInfo(inst).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        player.SendText("<color=#abd9fc>[PNJ debug]</color> " + line.TrimEnd('\r'));
                })).Register();

            Debug.Log($"[PNJCreator] {DisplayName} v{Version} — {Credit} — chargé ({Store.Config.Npcs.Count} PNJ, IA : {Store.Config.Ai.ActiveProvider}).");
        }

        // ------------------------------------------------------------------
        //  Accès
        // ------------------------------------------------------------------

        public bool CanEdit(Player player)
        {
            var a = Store.Config.Admin;
            if (!a.RequireAdmin) return true;
            if (player?.account == null || !player.IsAdmin) return false;
            if (player.account.AdminLevel < a.RequiredAdminLevel) return false;
            if (a.RequireAdminService && !player.IsAdminService) return false;
            return true;
        }

        public bool RequireEditor(Player player)
        {
            if (CanEdit(player)) return true;
            var a = Store.Config.Admin;
            player.Notify(DisplayName,
                a.RequireAdminService && player.IsAdmin
                    ? "Vous devez être en service admin."
                    : $"Réservé aux administrateurs (niveau {a.RequiredAdminLevel} minimum).",
                NotificationManager.Type.Error);
            return false;
        }

        public static Vector3 PlayerPosition(Player player) => player.setup.transform.position;
        public static float PlayerRotationY(Player player) => player.setup.transform.eulerAngles.y;
        public static string PlayerKey(Player player) => $"{player.steamId}:{player.character?.Id}";

        public void Reload()
        {
            if (Store.MemoryDirty) Store.SaveMemory();
            if (Store.UsageDirty) Store.SaveUsage();
            Npcs.DespawnAll();
            Store.LoadAll();
            if (NpcManager.ServerReady) Npcs.SpawnAll();
        }

        // ------------------------------------------------------------------
        //  /say
        // ------------------------------------------------------------------

        void Say(Player player, string rawMessage)
        {
            string message = Nova.RemoveTextFormat(rawMessage ?? "").Trim();
            if (message.Length == 0)
            {
                player.Notify(DisplayName, "Utilisation : /say <message>", NotificationManager.Type.Info);
                return;
            }
            if (message.Length > 300) message = message.Substring(0, 300);

            Vector3 pos = PlayerPosition(player);
            var inst = Npcs.Closest(pos, 50f, i => i.Data.AiEnabled && Vector3.Distance(pos, i.Position) <= i.Data.TalkRange);
            if (inst == null)
            {
                player.Notify(DisplayName, "Aucun PNJ à qui parler à proximité.", NotificationManager.Type.Warning);
                return;
            }

            var ai = Store.Config.Ai;
            string provider = ai.ActiveProvider;
            // Copie : la requête tourne en arrière-plan pendant qu'un admin peut modifier la config.
            ProviderSettings live = ai.Providers[provider];
            var settings = new ProviderSettings { ApiKey = live.ApiKey, Model = live.Model, BaseUrl = live.BaseUrl };
            if (string.IsNullOrEmpty(settings.ApiKey))
            {
                player.Notify(DisplayName, $"Aucune clé API {provider} configurée.", NotificationManager.Type.Error);
                return;
            }

            string key = PlayerKey(player);
            if (pendingSay.Contains(key))
            {
                player.Notify(DisplayName, $"{inst.Data.Name} réfléchit encore…", NotificationManager.Type.Info);
                return;
            }
            if (lastSay.TryGetValue(key, out float last) && Time.time - last < ai.SayCooldownSeconds)
            {
                player.Notify(DisplayName, "Patientez un instant avant de reparler.", NotificationManager.Type.Info);
                return;
            }
            lastSay[key] = Time.time;

            NpcData npc = inst.Data;
            string playerName = player.FullName;
            Nova.server.SendLocalText($"<color=#a4bbff>{playerName}</color> → <color={Store.Config.General.NameColor}>{npc.Name}</color> : {message}", npc.HearRange, pos);

            string userText = $"{playerName} te dit : {message}";
            var history = Store.GetHistory(npc.Id, key).Select(m => new ChatMessage(m.Role, m.Content)).ToList();
            history.Add(new ChatMessage("user", userText));
            string system = BuildSystemPrompt(npc);

            pendingSay.Add(key);
            AiClient.ChatAsync(provider, settings, system, history, ai.MaxOutputTokens, ai.RequestTimeoutSeconds)
                .ContinueWith(task =>
                {
                    AiResult result = task.Exception != null
                        ? new AiResult { Error = task.Exception.GetBaseException().Message }
                        : task.Result;
                    Runner.RunOnMainThread(() => OnAiReply(player, key, npc, provider, userText, result));
                });
        }

        void OnAiReply(Player player, string key, NpcData npc, string provider, string userText, AiResult result)
        {
            pendingSay.Remove(key);
            if (result.InputTokens > 0 || result.OutputTokens > 0) Store.AddUsage(npc, provider, result.InputTokens, result.OutputTokens);

            bool online = Nova.server.Players.Contains(player);
            if (!result.Success)
            {
                Debug.LogWarning($"[PNJCreator] IA ({provider}) pour le PNJ {npc.Id} : {result.Error}");
                if (online) player.Notify(DisplayName, $"{npc.Name} ne répond pas ({result.Error}).", NotificationManager.Type.Error, 8f);
                return;
            }

            string reply = CleanReply(result.Text, npc.Name);
            if (reply.Length == 0) return;
            Store.AppendExchange(npc.Id, key, userText, reply);

            Vector3 at = Npcs.Instances.TryGetValue(npc.Id, out var inst) ? inst.Position : npc.Position;
            Nova.server.SendLocalText($"<color={Store.Config.General.NameColor}>{npc.Name}</color> : {reply}", npc.HearRange, at);
        }

        string BuildSystemPrompt(NpcData npc)
        {
            string global = Store.Config.Ai.GlobalPrompt.Replace("{name}", npc.Name);
            string sex = npc.Sex == 1 ? "une femme" : "un homme";
            return $"{global}\n\nTu t'appelles {npc.Name} et tu es {sex}.\nTon rôle et ta personnalité : {npc.Prompt}";
        }

        string CleanReply(string text, string npcName)
        {
            string s = Nova.RemoveTextFormat(text ?? "").Replace("\r", " ").Replace("\n", " ").Replace("*", "").Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            // Certains modèles préfixent la réplique par leur nom.
            if (s.StartsWith(npcName + " :", StringComparison.OrdinalIgnoreCase)) s = s.Substring(npcName.Length + 2).Trim();
            else if (s.StartsWith(npcName + ":", StringComparison.OrdinalIgnoreCase)) s = s.Substring(npcName.Length + 1).Trim();
            s = s.Trim('"', '«', '»', ' ');
            int max = Store.Config.Ai.MaxReplyChars;
            if (s.Length > max) s = s.Substring(0, max).TrimEnd() + "…";
            return s;
        }
    }
}
