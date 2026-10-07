using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace PNJCreator
{
    // ------------------------------------------------------------------
    //  npcs.config.json
    // ------------------------------------------------------------------

    public class PluginConfig
    {
        public int ConfigVersion = 1;
        public AdminSettings Admin = new AdminSettings();
        public GeneralSettings General = new GeneralSettings();
        public AiSettings Ai = new AiSettings();
        /// <summary>Icônes des menus : clé -> id d'item (ex. "12") ou slug d'item. Vide = icône automatique ou aucune.</summary>
        public Dictionary<string, string> MenuIcons = new Dictionary<string, string>();
        public List<NpcData> Npcs = new List<NpcData>();
    }

    public class AdminSettings
    {
        /// <summary>Si false, tout joueur peut ouvrir l'éditeur (déconseillé).</summary>
        public bool RequireAdmin = true;
        /// <summary>Niveau admin minimum (account.adminLevel).</summary>
        public int RequiredAdminLevel = 1;
        /// <summary>Exiger que l'admin soit en service admin.</summary>
        public bool RequireAdminService = false;
    }

    public class GeneralSettings
    {
        /// <summary>Délai (s) après le démarrage du serveur avant de faire apparaître les PNJ.</summary>
        public float SpawnDelaySeconds = 10f;
        /// <summary>Couleur du nom au-dessus de la tête des PNJ.</summary>
        public string NameColor = "#f7af57";
    }

    public class AiSettings
    {
        /// <summary>OpenAI, DeepSeek ou Anthropic.</summary>
        public string ActiveProvider = AiProviders.OpenAI;
        public Dictionary<string, ProviderSettings> Providers = new Dictionary<string, ProviderSettings>();
        /// <summary>Instructions communes à tous les PNJ ({name} = nom du PNJ).</summary>
        public string GlobalPrompt =
            "Tu es {name}, un personnage non-joueur (PNJ) d'un serveur de jeu de rôle Nova-Life: Amboise, une ville française. " +
            "Reste toujours dans ton personnage et ne dis jamais que tu es une IA ou un PNJ. " +
            "Réponds uniquement en français, en 1 à 3 phrases courtes, comme dans un dialogue oral, sans emojis, sans listes et sans mise en forme. " +
            "Tu ne peux que parler : tu ne donnes ni argent, ni objets, et tu n'agis pas dans le jeu.";
        public float DefaultTalkRange = 4f;
        public float DefaultHearRange = 12f;
        /// <summary>Nombre de messages (joueur + PNJ) gardés en mémoire par joueur et par PNJ.</summary>
        public int MaxHistoryMessages = 16;
        public int MaxOutputTokens = 1024;
        public int MaxReplyChars = 400;
        public float SayCooldownSeconds = 3f;
        public int RequestTimeoutSeconds = 45;
    }

    public class ProviderSettings
    {
        public string ApiKey = "";
        public string Model = "";
        public string BaseUrl = "";
    }

    public static class AiProviders
    {
        public const string OpenAI = "OpenAI";
        public const string DeepSeek = "DeepSeek";
        public const string Anthropic = "Anthropic";
        public static readonly string[] All = { OpenAI, DeepSeek, Anthropic };

        public static string DefaultModel(string provider)
        {
            switch (provider)
            {
                case DeepSeek: return "deepseek-chat";
                case Anthropic: return "claude-opus-5-5";
                default: return "gpt-4o-mini";
            }
        }

        public static string DefaultBaseUrl(string provider)
        {
            switch (provider)
            {
                case DeepSeek: return "https://api.deepseek.com";
                case Anthropic: return "https://api.anthropic.com";
                default: return "https://api.openai.com/v1";
            }
        }
    }

    public class Waypoint
    {
        public float X, Y, Z;
        public Waypoint() { }
        public Waypoint(Vector3 v) { X = v.x; Y = v.y; Z = v.z; }
        [JsonIgnore] public Vector3 Position => new Vector3(X, Y, Z);
    }

    public class NpcData
    {
        public int Id;
        public string Name = "PNJ";
        /// <summary>0 = homme, 1 = femme.</summary>
        public int Sex;
        public float Scale = 1f;
        public float X, Y, Z;
        public float RotationY;

        // Déplacement
        public bool Mobile;
        public List<Waypoint> Waypoints = new List<Waypoint>();
        public float Speed = 1.4f;
        public float WaitSeconds = 3f;
        /// <summary>true = boucle (A→B→C→A), false = aller-retour (A→B→C→B→A).</summary>
        public bool LoopPatrol = true;

        // IA
        public bool AiEnabled;
        public string Prompt = "Tu es un habitant sympathique de la ville.";
        public float TalkRange = 4f;
        public float HearRange = 12f;

        /// <summary>Apparence (CharacterCustomizationSetup du jeu, au format JSON).</summary>
        public JToken Skin;

        [JsonIgnore] public Vector3 Position { get => new Vector3(X, Y, Z); set { X = value.x; Y = value.y; Z = value.z; } }
        [JsonIgnore] public string SexLabel => Sex == 1 ? "Femme" : "Homme";
    }

    // ------------------------------------------------------------------
    //  memory.json
    // ------------------------------------------------------------------

    public class ChatMessage
    {
        /// <summary>"user" ou "assistant".</summary>
        public string Role;
        public string Content;
        public ChatMessage() { }
        public ChatMessage(string role, string content) { Role = role; Content = content; }
    }

    public class MemoryData
    {
        /// <summary>Id PNJ -> clé joueur -> messages.</summary>
        public Dictionary<int, Dictionary<string, List<ChatMessage>>> Conversations = new Dictionary<int, Dictionary<string, List<ChatMessage>>>();
    }

    // ------------------------------------------------------------------
    //  usage.json
    // ------------------------------------------------------------------

    public class UsageCounter
    {
        public long InputTokens;
        public long OutputTokens;
        public long Requests;
        public void Add(long input, long output) { InputTokens += input; OutputTokens += output; Requests++; }
    }

    public class NpcUsage
    {
        public string Name = "";
        public UsageCounter Total = new UsageCounter();
        public Dictionary<string, UsageCounter> ByProvider = new Dictionary<string, UsageCounter>();
    }

    public class UsageData
    {
        public long SinceUnix;
        public UsageCounter Total = new UsageCounter();
        public Dictionary<string, UsageCounter> ByProvider = new Dictionary<string, UsageCounter>();
        public Dictionary<int, NpcUsage> ByNpc = new Dictionary<int, NpcUsage>();
    }

    // ------------------------------------------------------------------
    //  Lecture / écriture / normalisation
    // ------------------------------------------------------------------

    public class DataStore
    {
        public readonly string Folder;
        public string ConfigPath => Path.Combine(Folder, "npcs.config.json");
        public string MemoryPath => Path.Combine(Folder, "memory.json");
        public string UsagePath => Path.Combine(Folder, "usage.json");

        public PluginConfig Config = new PluginConfig();
        public MemoryData Memory = new MemoryData();
        public UsageData Usage = new UsageData();

        public bool MemoryDirty;
        public bool UsageDirty;

        public DataStore(string folder) { Folder = folder; }

        public void LoadAll()
        {
            Directory.CreateDirectory(Folder);
            Config = Load<PluginConfig>(ConfigPath) ?? new PluginConfig();
            Memory = Load<MemoryData>(MemoryPath) ?? new MemoryData();
            Usage = Load<UsageData>(UsagePath) ?? new UsageData();
            Normalize();
            SaveConfig();
            SaveMemory();
            SaveUsage();
        }

        static T Load<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            try
            {
                return JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                // Garde une copie du fichier illisible au lieu de l'écraser silencieusement.
                string backup = path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                try { File.Copy(path, backup, true); } catch { }
                Debug.LogError($"[PNJCreator] {Path.GetFileName(path)} illisible ({e.Message}). Copie : {backup}. Valeurs par défaut utilisées.");
                return null;
            }
        }

        static void Save(string path, object data)
        {
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(data, Formatting.Indented));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Debug.LogError($"[PNJCreator] Impossible d'écrire {Path.GetFileName(path)} : {e.Message}");
            }
        }

        public void SaveConfig() => Save(ConfigPath, Config);
        public void SaveMemory() { Save(MemoryPath, Memory); MemoryDirty = false; }
        public void SaveUsage() { Save(UsagePath, Usage); UsageDirty = false; }

        public void Normalize()
        {
            var c = Config;
            if (c.Admin == null) c.Admin = new AdminSettings();
            if (c.General == null) c.General = new GeneralSettings();
            if (c.Ai == null) c.Ai = new AiSettings();
            if (c.MenuIcons == null) c.MenuIcons = new Dictionary<string, string>();
            if (c.Npcs == null) c.Npcs = new List<NpcData>();

            c.Admin.RequiredAdminLevel = Mathf.Clamp(c.Admin.RequiredAdminLevel, 0, 100);
            c.General.SpawnDelaySeconds = Clamp(c.General.SpawnDelaySeconds, 0f, 300f, 10f);
            if (string.IsNullOrWhiteSpace(c.General.NameColor) || !c.General.NameColor.StartsWith("#")) c.General.NameColor = "#f7af57";

            var ai = c.Ai;
            if (ai.Providers == null) ai.Providers = new Dictionary<string, ProviderSettings>();
            // Clés de fournisseurs insensibles à la casse dans le fichier.
            var fixedProviders = new Dictionary<string, ProviderSettings>();
            foreach (string name in AiProviders.All)
            {
                var found = ai.Providers.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value ?? new ProviderSettings();
                if (found.ApiKey == null) found.ApiKey = "";
                found.ApiKey = found.ApiKey.Trim();
                if (string.IsNullOrWhiteSpace(found.Model)) found.Model = AiProviders.DefaultModel(name);
                found.Model = found.Model.Trim();
                if (string.IsNullOrWhiteSpace(found.BaseUrl) || !found.BaseUrl.StartsWith("http")) found.BaseUrl = AiProviders.DefaultBaseUrl(name);
                found.BaseUrl = found.BaseUrl.Trim().TrimEnd('/');
                fixedProviders[name] = found;
            }
            ai.Providers = fixedProviders;
            ai.ActiveProvider = AiProviders.All.FirstOrDefault(p => string.Equals(p, ai.ActiveProvider, StringComparison.OrdinalIgnoreCase)) ?? AiProviders.OpenAI;
            if (string.IsNullOrWhiteSpace(ai.GlobalPrompt)) ai.GlobalPrompt = new AiSettings().GlobalPrompt;
            ai.DefaultTalkRange = Clamp(ai.DefaultTalkRange, 1f, 50f, 4f);
            ai.DefaultHearRange = Clamp(ai.DefaultHearRange, 1f, 100f, 12f);
            ai.MaxHistoryMessages = Mathf.Clamp(ai.MaxHistoryMessages, 0, 100);
            ai.MaxOutputTokens = Mathf.Clamp(ai.MaxOutputTokens <= 0 ? 1024 : ai.MaxOutputTokens, 64, 8192);
            ai.MaxReplyChars = Mathf.Clamp(ai.MaxReplyChars <= 0 ? 400 : ai.MaxReplyChars, 50, 2000);
            ai.SayCooldownSeconds = Clamp(ai.SayCooldownSeconds, 0f, 60f, 3f);
            ai.RequestTimeoutSeconds = Mathf.Clamp(ai.RequestTimeoutSeconds <= 0 ? 45 : ai.RequestTimeoutSeconds, 5, 300);

            var usedIds = new HashSet<int>();
            int nextId = c.Npcs.Count == 0 ? 1 : Math.Max(1, c.Npcs.Where(n => n != null).Select(n => n.Id).DefaultIfEmpty(0).Max() + 1);
            c.Npcs.RemoveAll(n => n == null);
            foreach (var n in c.Npcs)
            {
                if (n.Id <= 0 || !usedIds.Add(n.Id)) { n.Id = nextId++; usedIds.Add(n.Id); }
                n.Name = string.IsNullOrWhiteSpace(n.Name) ? "PNJ " + n.Id : n.Name.Trim();
                if (n.Name.Length > 40) n.Name = n.Name.Substring(0, 40);
                n.Sex = n.Sex == 1 ? 1 : 0;
                n.Scale = Clamp(n.Scale, 0.5f, 2f, 1f);
                if (!IsFinite(n.X) || !IsFinite(n.Y) || !IsFinite(n.Z)) { n.X = 0; n.Y = 0; n.Z = 0; }
                n.RotationY = IsFinite(n.RotationY) ? Mathf.Repeat(n.RotationY, 360f) : 0f;
                if (n.Waypoints == null) n.Waypoints = new List<Waypoint>();
                n.Waypoints.RemoveAll(w => w == null || !IsFinite(w.X) || !IsFinite(w.Y) || !IsFinite(w.Z));
                n.Speed = Clamp(n.Speed, 0.3f, 8f, 1.4f);
                n.WaitSeconds = Clamp(n.WaitSeconds, 0f, 600f, 3f);
                if (n.Prompt == null) n.Prompt = "";
                n.TalkRange = Clamp(n.TalkRange, 1f, 50f, ai.DefaultTalkRange);
                n.HearRange = Clamp(n.HearRange, 1f, 100f, ai.DefaultHearRange);
                if (n.Skin != null && n.Skin.Type != JTokenType.Object) n.Skin = null;
            }

            if (Memory == null) Memory = new MemoryData();
            if (Memory.Conversations == null) Memory.Conversations = new Dictionary<int, Dictionary<string, List<ChatMessage>>>();
            if (Usage == null) Usage = new UsageData();
            if (Usage.Total == null) Usage.Total = new UsageCounter();
            if (Usage.ByProvider == null) Usage.ByProvider = new Dictionary<string, UsageCounter>();
            if (Usage.ByNpc == null) Usage.ByNpc = new Dictionary<int, NpcUsage>();
            if (Usage.SinceUnix <= 0) Usage.SinceUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        static bool IsFinite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);

        static float Clamp(float v, float min, float max, float fallback)
        {
            if (!IsFinite(v) || v <= 0f && min > 0f) return fallback;
            return Mathf.Clamp(v, min, max);
        }

        public int NextNpcId() => Config.Npcs.Count == 0 ? 1 : Config.Npcs.Max(n => n.Id) + 1;

        public void AddUsage(NpcData npc, string provider, long input, long output)
        {
            Usage.Total.Add(input, output);
            if (!Usage.ByProvider.TryGetValue(provider, out var p)) Usage.ByProvider[provider] = p = new UsageCounter();
            p.Add(input, output);
            if (!Usage.ByNpc.TryGetValue(npc.Id, out var n)) Usage.ByNpc[npc.Id] = n = new NpcUsage();
            n.Name = npc.Name;
            n.Total.Add(input, output);
            if (!n.ByProvider.TryGetValue(provider, out var np)) n.ByProvider[provider] = np = new UsageCounter();
            np.Add(input, output);
            UsageDirty = true;
        }

        public void ResetUsage()
        {
            Usage = new UsageData { SinceUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            SaveUsage();
        }

        public List<ChatMessage> GetHistory(int npcId, string playerKey)
        {
            if (!Memory.Conversations.TryGetValue(npcId, out var byPlayer))
                Memory.Conversations[npcId] = byPlayer = new Dictionary<string, List<ChatMessage>>();
            if (!byPlayer.TryGetValue(playerKey, out var list))
                byPlayer[playerKey] = list = new List<ChatMessage>();
            return list;
        }

        public void AppendExchange(int npcId, string playerKey, string userText, string reply)
        {
            var list = GetHistory(npcId, playerKey);
            list.Add(new ChatMessage("user", userText));
            list.Add(new ChatMessage("assistant", reply));
            int max = Config.Ai.MaxHistoryMessages;
            // On retire par paires pour toujours commencer par un message "user".
            while (list.Count > max && list.Count >= 2) list.RemoveRange(0, 2);
            if (max == 0) list.Clear();
            MemoryDirty = true;
        }

        public void ClearMemory(int npcId)
        {
            Memory.Conversations.Remove(npcId);
            MemoryDirty = true;
        }
    }
}
