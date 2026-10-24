using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Life;
using Life.DB;
using Life.Network;
using Life.UI;
using Mirror;
using ModKit.Helper;
using ModKit.Helper.DiscordHelper;
using ModKit.Helper.PointHelper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.ORM;
using ModKit.Utils;
using Newtonsoft.Json;
using SQLite;
using UnityEngine;
using static ModKit.Helper.TextFormattingHelper;
using Logger = ModKit.Internal.Logger;

namespace WhitelistNova
{
    /// <summary>
    /// Whitelist.nova - by Loris Strange &amp; Matheo Mercier.
    ///
    /// À chaque connexion, un joueur non whitelisté est téléporté et bloqué sur un des
    /// lieux d'attente choisis par le staff, puis remplit un questionnaire entièrement
    /// personnalisable. Sa candidature est envoyée au staff (Discord + en jeu). Si elle est
    /// acceptée, le joueur reçoit un code de validation unique qu'il doit saisir sur un
    /// point bleu « Whitelist » : il est alors téléporté à la destination de ce point.
    /// </summary>
    public class WhitelistPlugin : ModKit.ModKit
    {
        public const string Title = "Whitelist";

        public static WhitelistPlugin Instance { get; private set; }

        public WlConfig Config { get; private set; } = new WlConfig();

        private string configPath;

        /// <summary>Joueurs bloqués (clé : id du compte) et lieu d'attente qui leur a été attribué.</summary>
        private readonly Dictionary<int, Vector3?> blocked = new Dictionary<int, Vector3?>();

        /// <summary>Réponses en cours de saisie (clé : id du compte, puis id de la question).</summary>
        private readonly Dictionary<int, Dictionary<int, string>> drafts = new Dictionary<int, Dictionary<int, string>>();

        /// <summary>Essais de code ratés (clé : id du compte) pour éviter de deviner les codes.</summary>
        private readonly Dictionary<int, (int count, long until)> codeAttempts = new Dictionary<int, (int count, long until)>();

        /// <summary>Positions des points de saisie du code (zone autorisée pour les joueurs acceptés).</summary>
        private List<Vector3> codePointPositions = new List<Vector3>();

        /// <summary>Staff en mode test : ils suivent le parcours complet d'un nouveau joueur.</summary>
        private readonly HashSet<int> testers = new HashSet<int>();

        public const string TestTag = "[TEST] ";

        public WhitelistPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "Loris Strange & Matheo Mercier");
            Instance = this;
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            configPath = Path.Combine(pluginsPath, "Whitelist.nova", "config.json");
            LoadConfig();

            Orm.RegisterTable<WlQuestion>();
            Orm.RegisterTable<WlCandidature>();
            Orm.RegisterTable<WlWaitPoint>();
            Orm.RegisterTable<WlCodePattern>();

            // Point bleu de saisie du code : AAMenu > Administration > Points bleus
            WlCodePattern pattern = new WlCodePattern(false);
            PointHelper.AddPattern(nameof(WlCodePattern), pattern);

            if (AAMenu.AAMenu.menu != null)
            {
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, "Whitelist - Saisie du code", pattern, this);
                AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, Config.MinAdminLevel, "Whitelist", ui =>
                {
                    Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                    if (player != null) OpenStaffMenu(player);
                });
            }
            else
            {
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : utilisez la commande /wladmin.");
            }

            RegisterCommands();
            _ = CreateDefaultQuestions();
            _ = RefreshCodePoints();

            Nova.server.OnMinutePassedEvent += OnMinutePassed;
            Nova.man.StartCoroutine(ConfinementLoop());

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", "initialisé - by Loris Strange & Matheo Mercier");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            PointHelper.InitAllNPoint(player);
            Nova.man.StartCoroutine(HandleSpawnDelayed(player));
        }

        // ==================================================================
        //  Configuration
        // ==================================================================

        public void LoadConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                if (File.Exists(configPath))
                    Config = JsonConvert.DeserializeObject<WlConfig>(File.ReadAllText(configPath)) ?? new WlConfig();
                SaveConfig();
            }
            catch (Exception e)
            {
                Logger.LogError(PluginInformations.SourceName, $"Lecture de {configPath} impossible : {e.Message}");
                Config = new WlConfig();
            }
        }

        public void SaveConfig()
        {
            try
            {
                File.WriteAllText(configPath, JsonConvert.SerializeObject(Config, Formatting.Indented));
            }
            catch (Exception e)
            {
                Logger.LogError(PluginInformations.SourceName, $"Écriture de {configPath} impossible : {e.Message}");
            }
        }

        // ==================================================================
        //  Commandes
        // ==================================================================

        private void RegisterCommands()
        {
            new SChatCommand("/wl", new[] { "/whitelist" }, "Remplir / suivre sa candidature whitelist", "/wl",
                (Action<Player, string[]>)(async (player, args) => await OpenPlayerMenu(player))).Register();

            new SChatCommand("/wladmin", "Gérer la whitelist (staff)", "/wladmin",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!IsStaff(player))
                    {
                        player.Notify(Title, "Vous n'avez pas la permission.", NotificationManager.Type.Error, 5f);
                        return;
                    }
                    OpenStaffMenu(player);
                })).Register();
        }

        // ==================================================================
        //  Connexion et blocage des joueurs
        // ==================================================================

        private IEnumerator HandleSpawnDelayed(Player player)
        {
            // Laisse le jeu placer le personnage avant de le déplacer
            yield return new WaitForSeconds(Mathf.Max(0.5f, Config.SpawnDelaySeconds));
            _ = HandleSpawn(player);
        }

        private async Task HandleSpawn(Player player)
        {
            try
            {
                if (!Config.Enabled || !IsOnline(player)) return;
                if (Config.StaffBypass && player.IsAdmin) return;

                WlCandidature last = await GetLastCandidature(player.account.id);
                if (last != null && last.Status == WlStatus.Validated) return;

                Block(player, last);

                if (last == null || last.Status == WlStatus.Revoked)
                    await StartQuestionnaire(player);
                else if (last.Status == WlStatus.Refused)
                    ShowRefused(player, last);
                else if (last.Status == WlStatus.Pending)
                    player.Notify(Title, "Votre candidature est en cours d'examen par le staff. Patientez ici.", NotificationManager.Type.Info, 10f);
                else if (last.Status == WlStatus.Accepted)
                    ShowCode(player, last);
            }
            catch (Exception e)
            {
                Logger.LogError(PluginInformations.SourceName, $"Erreur à la connexion de {player?.FullName} : {e}");
            }
        }

        /// <summary>Téléporte le joueur sur un lieu d'attente au hasard et le gèle.</summary>
        private async void Block(Player player, WlCandidature last)
        {
            int accountId = player.account.id;
            List<WlWaitPoint> points = await WlWaitPoint.QueryAll();
            Vector3? spot = null;
            if (points.Count > 0)
            {
                spot = points[UnityEngine.Random.Range(0, points.Count)].GetPosition();
                player.setup.TargetSetPosition(spot.Value);
            }
            else
            {
                Logger.LogWarning(PluginInformations.SourceName, "Aucun lieu d'attente configuré (/wladmin > Lieux d'attente).");
            }

            blocked[accountId] = spot;
            // Les joueurs acceptés peuvent marcher jusqu'au point de saisie du code
            SetFrozen(player, Config.FreezePlayers && (last == null || last.Status != WlStatus.Accepted));
        }

        public void Unblock(Player player)
        {
            if (player?.account == null) return;
            blocked.Remove(player.account.id);
            testers.Remove(player.account.id);
            drafts.Remove(player.account.id);
            SetFrozen(player, false);
        }

        private static void SetFrozen(Player player, bool frozen)
        {
            if (player?.setup != null) player.IsFreezed = frozen;
        }

        public bool IsBlocked(Player player) => player?.account != null && blocked.ContainsKey(player.account.id);

        /// <summary>Ramène sur son lieu d'attente un joueur bloqué qui s'en éloigne trop.</summary>
        private IEnumerator ConfinementLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(Mathf.Max(1f, Config.CheckIntervalSeconds));
                try
                {
                    CheckConfinement();
                }
                catch (Exception e)
                {
                    Logger.LogError(PluginInformations.SourceName, $"Erreur de confinement : {e.Message}");
                }
            }
        }

        private void CheckConfinement()
        {
            if (!Config.Enabled || blocked.Count == 0) return;

            foreach (Player player in Nova.server.Players.ToList())
            {
                if (!IsOnline(player) || !blocked.TryGetValue(player.account.id, out Vector3? spot) || spot == null)
                    continue;

                Vector3 position = player.setup.transform.position;
                bool allowed = Vector3.Distance(position, spot.Value) <= Config.MaxDistance
                    || codePointPositions.Any(p => Vector3.Distance(position, p) <= Config.MaxDistance);
                if (allowed) continue;

                player.setup.TargetSetPosition(spot.Value);
                player.Notify(Title, "Vous devez être whitelisté pour quitter cette zone.", NotificationManager.Type.Warning, 5f);
            }
        }

        /// <summary>Positions des points de saisie du code, autorisées aux joueurs acceptés.</summary>
        private async Task RefreshCodePoints()
        {
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == nameof(WlCodePattern));
            codePointPositions = points.Select(p => p.VPosition).ToList();
        }

        private async void OnMinutePassed()
        {
            try
            {
                await RefreshCodePoints();

                foreach (Player player in Nova.server.Players.ToList())
                {
                    if (!IsOnline(player) || !IsBlocked(player)) continue;
                    WlCandidature last = await GetLastCandidature(player.account.id);
                    if (last == null || last.Status == WlStatus.Revoked || (last.Status == WlStatus.Refused && Config.AllowRetryAfterRefuse))
                        player.Notify(Title, "Tapez /wl pour remplir votre candidature whitelist.", NotificationManager.Type.Info, 8f);
                    else if (last.Status == WlStatus.Accepted)
                        player.Notify(Title, $"Votre code : {last.Code}. Entrez-le sur le point de validation (/wl pour le revoir).", NotificationManager.Type.Success, 8f);
                }
            }
            catch (Exception e)
            {
                Logger.LogError(PluginInformations.SourceName, $"Erreur OnMinutePassed : {e.Message}");
            }
        }

        // ==================================================================
        //  Côté joueur : questionnaire
        // ==================================================================

        /// <summary>/wl : ouvre le questionnaire ou affiche l'état de la candidature.</summary>
        public async Task OpenPlayerMenu(Player player)
        {
            if (!IsOnline(player)) return;
            WlCandidature last = await GetLastCandidature(player.account.id);

            if (last != null && last.Status == WlStatus.Validated && !IsTesting(player))
                player.Notify(Title, "Vous êtes déjà whitelisté.", NotificationManager.Type.Success, 5f);
            else if (last == null || last.Status == WlStatus.Revoked || last.Status == WlStatus.Validated)
                await StartQuestionnaire(player);
            else if (last.Status == WlStatus.Pending)
                player.Notify(Title, "Votre candidature est en cours d'examen par le staff.", NotificationManager.Type.Info, 5f);
            else if (last.Status == WlStatus.Accepted)
                ShowCode(player, last);
            else if (last.Status == WlStatus.Refused)
                ShowRefused(player, last);
        }

        public async Task StartQuestionnaire(Player player)
        {
            List<WlQuestion> questions = await GetQuestions();
            if (questions.Count == 0)
            {
                player.Notify(Title, "Le questionnaire n'est pas encore configuré. Merci de patienter.", NotificationManager.Type.Warning, 8f);
                NotifyStaff("Un joueur attend mais aucune question n'est configurée (/wladmin > Questions).");
                return;
            }

            Panel panel = PanelHelper.Create(Config.FormTitle, UIPanel.PanelType.Text, player, () => _ = StartQuestionnaire(player));
            foreach (string line in SplitLines(Config.WelcomeText))
                panel.TextLines.Add(line);
            panel.TextLines.Add("");
            panel.TextLines.Add(Color($"{questions.Count} question(s)", Colors.Info));

            panel.AddButton(Color("Commencer", Colors.Success), _ => AskQuestion(player, questions, 0));
            panel.CloseButton();
            panel.Display();
        }

        private Dictionary<int, string> GetDraft(Player player)
        {
            if (!drafts.TryGetValue(player.account.id, out Dictionary<int, string> answers))
                drafts[player.account.id] = answers = new Dictionary<int, string>();
            return answers;
        }

        private void AskQuestion(Player player, List<WlQuestion> questions, int index)
        {
            if (index >= questions.Count)
            {
                ShowSummary(player, questions);
                return;
            }

            WlQuestion question = questions[index];
            Dictionary<int, string> answers = GetDraft(player);
            answers.TryGetValue(question.Id, out string previous);
            string title = $"{Config.FormTitle} ({index + 1}/{questions.Count})";

            if (question.IsChoice)
            {
                Panel panel = PanelHelper.Create(title, UIPanel.PanelType.Tab, player, () => AskQuestion(player, questions, index));
                panel.TextLines.Add(question.Text);
                foreach (string choice in question.GetChoices())
                {
                    string label = choice == previous ? Color(choice, Colors.Success) : choice;
                    panel.AddTabLine(label, _ =>
                    {
                        answers[question.Id] = choice;
                        AskQuestion(player, questions, index + 1);
                    });
                }
                panel.AddButton("Choisir", _ => panel.SelectTab());
                if (!question.Required)
                    panel.AddButton("Passer", _ => { answers.Remove(question.Id); AskQuestion(player, questions, index + 1); });
                if (index > 0)
                    panel.AddButton("Précédent", _ => AskQuestion(player, questions, index - 1));
                panel.CloseButton();
                panel.Display();
            }
            else
            {
                Panel panel = PanelHelper.Create(title, UIPanel.PanelType.Input, player, () => AskQuestion(player, questions, index));
                panel.TextLines.Add(question.Text);
                if (question.MinLength > 0)
                    panel.TextLines.Add(Color($"Minimum {question.MinLength} caractères.", Colors.Grey));
                if (!string.IsNullOrEmpty(previous))
                    panel.TextLines.Add(Color($"Réponse actuelle : {previous}", Colors.Grey));
                panel.SetInputPlaceholder(string.IsNullOrEmpty(question.Placeholder) ? "Votre réponse..." : question.Placeholder);

                panel.AddButton(Color("Suivant", Colors.Success), _ =>
                {
                    string answer = panel.inputText?.Trim();
                    if (string.IsNullOrEmpty(answer) && !string.IsNullOrEmpty(previous))
                        answer = previous;

                    if (string.IsNullOrEmpty(answer))
                    {
                        if (question.Required)
                        {
                            player.Notify(Title, "Cette question est obligatoire.", NotificationManager.Type.Error, 5f);
                            return;
                        }
                        answers.Remove(question.Id);
                    }
                    else if (answer.Length < question.MinLength)
                    {
                        player.Notify(Title, $"Réponse trop courte ({answer.Length}/{question.MinLength} caractères).", NotificationManager.Type.Error, 5f);
                        return;
                    }
                    else
                    {
                        answers[question.Id] = answer;
                    }
                    AskQuestion(player, questions, index + 1);
                });
                if (index > 0)
                    panel.AddButton("Précédent", _ => AskQuestion(player, questions, index - 1));
                panel.CloseButton();
                panel.Display();
            }
        }

        private void ShowSummary(Player player, List<WlQuestion> questions)
        {
            Dictionary<int, string> answers = GetDraft(player);

            // Liste à onglets (défilante) : sélectionner une ligne permet de modifier la réponse
            Panel panel = PanelHelper.Create($"{Config.FormTitle} - Récapitulatif", UIPanel.PanelType.Tab, player, () => ShowSummary(player, questions));
            for (int i = 0; i < questions.Count; i++)
            {
                int index = i;
                answers.TryGetValue(questions[i].Id, out string answer);
                string shown = string.IsNullOrEmpty(answer) ? Color("(sans réponse)", Colors.Grey) : Truncate(answer, 40);
                panel.AddTabLine($"{i + 1}. {Truncate(questions[i].Text, 35)} : {shown}", _ => AskQuestion(player, questions, index));
            }

            panel.AddButton(Color("Envoyer", Colors.Success), ui => _ = Submit(player, questions, panel));
            panel.AddButton("Modifier", _ => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        private async Task Submit(Player player, List<WlQuestion> questions, Panel panel)
        {
            Dictionary<int, string> answers = GetDraft(player);
            WlQuestion missing = questions.FirstOrDefault(q => q.Required && !answers.ContainsKey(q.Id));
            if (missing != null)
            {
                player.Notify(Title, $"Question obligatoire sans réponse : {missing.Text}", NotificationManager.Type.Error, 6f);
                AskQuestion(player, questions, questions.IndexOf(missing));
                return;
            }

            bool isTest = IsTesting(player);
            WlCandidature last = await GetLastCandidature(player.account.id);
            if (!isTest && last != null && (last.Status == WlStatus.Pending || last.Status == WlStatus.Accepted || last.Status == WlStatus.Validated))
            {
                player.Notify(Title, "Vous avez déjà une candidature en cours.", NotificationManager.Type.Warning, 5f);
                panel.Close();
                return;
            }

            WlCandidature candidature = new WlCandidature
            {
                AccountId = player.account.id,
                CharacterId = player.character?.Id ?? 0,
                SteamId = player.steamId.ToString(),
                SteamName = player.steamUsername,
                PlayerName = (isTest ? TestTag : "") + player.FullName,
                Status = WlStatus.Pending,
                CreatedAt = Nova.UnixTimeNow()
            };
            candidature.SetAnswers(questions
                .Select(q => new WlAnswer { Question = q.Text, Answer = answers.TryGetValue(q.Id, out string a) ? a : "" })
                .ToList());

            if (!await candidature.Save())
            {
                player.Notify(Title, "Erreur lors de l'envoi, réessayez.", NotificationManager.Type.Error, 5f);
                return;
            }

            drafts.Remove(player.account.id);
            panel.Close();
            player.Notify(Title, Config.EndText, NotificationManager.Type.Success, 10f);

            NotifyStaff($"Nouvelle candidature de {candidature.PlayerName} (#{candidature.Id}). Ouvrez /wladmin ou AAMenu > Whitelist.");
            if (isTest)
                player.Notify(Title, "[TEST] Étape suivante : /wladmin > Candidatures en attente > acceptez votre propre candidature.", NotificationManager.Type.Info, 12f);
            await SendDiscordCandidature(candidature);
        }

        private void ShowCode(Player player, WlCandidature candidature)
        {
            SetFrozen(player, false);
            Panel panel = PanelHelper.Create($"{Config.FormTitle} - Acceptée", UIPanel.PanelType.Text, player, () => ShowCode(player, candidature));
            foreach (string line in SplitLines(Config.AcceptedText))
                panel.TextLines.Add(line);
            panel.TextLines.Add("");
            panel.TextLines.Add("Votre code de validation :");
            panel.TextLines.Add(Size(Bold(Color(candidature.Code, Colors.Success)), 28));
            panel.CloseButton("OK");
            panel.Display();
            player.SendText($"<color=#2ecc71>[Whitelist] Votre code de validation : {candidature.Code}</color>");
        }

        private void ShowRefused(Player player, WlCandidature candidature)
        {
            Panel panel = PanelHelper.Create($"{Config.FormTitle} - Refusée", UIPanel.PanelType.Text, player, () => ShowRefused(player, candidature));
            panel.TextLines.Add(Color("Votre candidature a été refusée.", Colors.Error));
            if (!string.IsNullOrEmpty(candidature.RefuseReason))
                panel.TextLines.Add($"Raison : {candidature.RefuseReason}");
            if (Config.AllowRetryAfterRefuse)
            {
                panel.TextLines.Add("Vous pouvez envoyer une nouvelle candidature.");
                panel.AddButton(Color("Recommencer", Colors.Success), ui => _ = StartQuestionnaire(player));
            }
            panel.CloseButton();
            panel.Display();
        }

        // ==================================================================
        //  Côté joueur : saisie du code sur le point bleu
        // ==================================================================

        public async Task OpenCodeInput(Player player, WlCodePattern point)
        {
            if (!IsOnline(player)) return;
            WlCandidature last = await GetLastCandidature(player.account.id);

            if (last != null && last.Status == WlStatus.Validated)
            {
                player.Notify(Title, "Vous êtes déjà whitelisté.", NotificationManager.Type.Info, 5f);
                return;
            }
            if (last == null || last.Status != WlStatus.Accepted)
            {
                player.Notify(Title, "Vous n'avez pas encore de code : votre candidature doit d'abord être acceptée.", NotificationManager.Type.Warning, 6f);
                return;
            }

            Panel panel = PanelHelper.Create(point.PatternName ?? "Validation whitelist", UIPanel.PanelType.Input, player, () => _ = OpenCodeInput(player, point));
            panel.TextLines.Add("Entrez le code de validation reçu par le staff.");
            panel.SetInputPlaceholder($"{Config.CodePrefix}XXXXXX");
            panel.AddButton(Color("Valider", Colors.Success), ui => _ = CheckCode(player, point, panel));
            panel.CloseButton();
            panel.Display();
        }

        private async Task CheckCode(Player player, WlCodePattern point, Panel panel)
        {
            int accountId = player.account.id;
            long now = Nova.UnixTimeNow();
            if (codeAttempts.TryGetValue(accountId, out var attempts) && attempts.until > now)
            {
                player.Notify(Title, $"Trop d'essais. Réessayez dans {attempts.until - now} s.", NotificationManager.Type.Error, 5f);
                return;
            }

            WlCandidature last = await GetLastCandidature(accountId);
            string input = NormalizeCode(panel.inputText);
            if (last == null || last.Status != WlStatus.Accepted || input.Length == 0 || input != NormalizeCode(last.Code))
            {
                int count = attempts.count + 1;
                long until = count >= Config.MaxCodeAttempts ? now + 60 : 0;
                codeAttempts[accountId] = (until > 0 ? 0 : count, until);
                player.Notify(Title, "Code invalide.", NotificationManager.Type.Error, 5f);
                return;
            }

            Vector3? destination = point.GetDestination();
            if (destination == null)
            {
                player.Notify(Title, "Ce point n'a pas de destination. Contactez le staff.", NotificationManager.Type.Error, 6f);
                NotifyStaff($"Le point whitelist « {point.PatternName} » n'a pas de destination configurée.");
                return;
            }

            last.Status = WlStatus.Validated;
            last.ValidatedAt = now;
            if (!await last.Save())
            {
                player.Notify(Title, "Erreur lors de la validation, réessayez.", NotificationManager.Type.Error, 5f);
                return;
            }

            codeAttempts.Remove(accountId);
            bool wasTest = IsTesting(player);
            Unblock(player);
            panel.Close();
            player.setup.TargetSetPosition(destination.Value);
            player.Notify(Title, "Code validé ! Bienvenue sur le serveur, bon jeu !", NotificationManager.Type.Success, 10f);
            if (wasTest)
                player.Notify(Title, "[TEST] Test terminé avec succès : tout le parcours fonctionne.", NotificationManager.Type.Success, 12f);

            NotifyStaff($"{player.FullName} a validé son code et est maintenant whitelisté.");
            await SendDiscordLog("Joueur whitelisté", $"**{last.PlayerName}** a saisi son code de validation et est entré sur le serveur.", "#2ecc71", last);
        }

        private static string NormalizeCode(string code)
        {
            return new string((code ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }

        // ==================================================================
        //  Côté staff : menu principal
        // ==================================================================

        public bool IsStaff(Player player)
        {
            return player != null && player.IsAdmin && player.account != null && player.account.AdminLevel >= Config.MinAdminLevel;
        }

        public async void OpenStaffMenu(Player player)
        {
            if (!IsStaff(player)) return;

            List<WlCandidature> pending = await WlCandidature.Query(c => c.Status == WlStatus.Pending);
            int questions = (await WlQuestion.QueryAll()).Count;
            int waitPoints = (await WlWaitPoint.QueryAll()).Count;

            Panel panel = PanelHelper.Create("Whitelist.nova - Staff", UIPanel.PanelType.Tab, player, () => OpenStaffMenu(player));
            panel.AddTabLine($"Candidatures en attente ({Color(pending.Count.ToString(), pending.Count > 0 ? Colors.Warning : Colors.Grey)})", ui => _ = ShowCandidatures(player, true));
            panel.AddTabLine("Historique des candidatures", ui => _ = ShowCandidatures(player, false));
            panel.AddTabLine($"Questionnaire ({questions} question(s))", ui => _ = ShowQuestions(player));
            panel.AddTabLine($"Lieux d'attente ({waitPoints})", ui => _ = ShowWaitPoints(player));
            panel.AddTabLine("Points de saisie du code", _ => new WlCodePattern(false) { Context = this }.CreateOrGenerate(player));
            panel.AddTabLine("Joueurs connectés (whitelister / révoquer)", ui => _ = ShowOnlinePlayers(player));
            panel.AddTabLine("Paramètres", _ => ShowSettings(player));
            panel.AddTabLine(Color(IsTesting(player) ? "Mode test (en cours)" : "Tester le plugin sur moi", Colors.Purple), ui => _ = ShowTestMenu(player));

            panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Candidatures
        // ------------------------------------------------------------------

        private async Task ShowCandidatures(Player player, bool pendingOnly)
        {
            List<WlCandidature> list = pendingOnly
                ? await WlCandidature.Query(c => c.Status == WlStatus.Pending)
                : (await WlCandidature.Query(c => c.Status != WlStatus.Pending)).OrderByDescending(c => c.Id).Take(Config.HistorySize).ToList();

            Panel panel = PanelHelper.Create(pendingOnly ? "Whitelist - Candidatures en attente" : "Whitelist - Historique",
                UIPanel.PanelType.Tab, player, () => _ = ShowCandidatures(player, pendingOnly));

            if (list.Count == 0)
                panel.AddTabLine("Aucune candidature", _ => { });
            foreach (WlCandidature c in list)
            {
                string date = Nova.UnixTimeToDateTime(c.CreatedAt).ToLocalTime().ToString("dd/MM HH:mm");
                panel.AddTabLine($"#{c.Id} {c.PlayerName} - {date} - {WlStatus.Label(c.Status)}", _ => ShowCandidature(player, c));
            }

            if (list.Count > 0)
                panel.NextButton("Voir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void ShowCandidature(Player player, WlCandidature c)
        {
            // Liste à onglets (défilante) : sélectionner une réponse l'affiche en entier
            Panel panel = PanelHelper.Create($"Candidature #{c.Id} - {c.PlayerName}", UIPanel.PanelType.Tab, player, () => ShowCandidature(player, c));
            panel.AddTabLine($"Statut : {WlStatus.Label(c.Status)}", _ => { });
            panel.AddTabLine(Color($"Steam : {c.SteamName} ({c.SteamId})", Colors.Grey), _ => { });
            if (!string.IsNullOrEmpty(c.ReviewedBy))
                panel.AddTabLine(Color($"Traitée par : {c.ReviewedBy}", Colors.Grey), _ => { });
            if (!string.IsNullOrEmpty(c.Code))
                panel.AddTabLine($"Code : {Bold(c.Code)}", _ => { });
            if (!string.IsNullOrEmpty(c.RefuseReason))
                panel.AddTabLine($"Refus : {Truncate(c.RefuseReason, 50)}", _ => ShowText(player, "Raison du refus", c.RefuseReason));

            List<WlAnswer> answers = c.GetAnswers();
            for (int i = 0; i < answers.Count; i++)
            {
                WlAnswer answer = answers[i];
                int number = i + 1;
                string shown = string.IsNullOrEmpty(answer.Answer) ? Color("(sans réponse)", Colors.Grey) : Truncate(answer.Answer, 40);
                panel.AddTabLine($"{Color($"Q{i + 1}.", Colors.Info)} {Truncate(answer.Question, 30)} : {shown}", _ =>
                    ShowText(player, $"Question {number}", Bold(Color(answer.Question, Colors.Info)), string.IsNullOrEmpty(answer.Answer) ? "(sans réponse)" : answer.Answer));
            }
            panel.AddButton("Lire", _ => panel.SelectTab());

            if (c.Status == WlStatus.Pending)
            {
                panel.PreviousButtonWithAction(Color("Accepter", Colors.Success), async () => await Accept(player, c));
                panel.NextButton(Color("Refuser", Colors.Error), () => AskRefuseReason(player, c));
            }
            else if (c.Status == WlStatus.Accepted)
            {
                panel.PreviousButtonWithAction("Nouveau code", async () => await Accept(player, c));
            }
            if (c.Status == WlStatus.Accepted || c.Status == WlStatus.Validated)
                panel.PreviousButtonWithAction(Color("Révoquer", Colors.Error), async () => await Revoke(player, c));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> Accept(Player staff, WlCandidature c)
        {
            c.Status = WlStatus.Accepted;
            c.Code = await GenerateUniqueCode();
            c.ReviewedBy = staff.FullName;
            c.ReviewedAt = Nova.UnixTimeNow();
            if (!await c.Save())
            {
                staff.Notify(Title, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
                return false;
            }

            staff.Notify(Title, $"Candidature de {c.PlayerName} acceptée. Code : {c.Code}", NotificationManager.Type.Success, 10f);
            Player target = FindOnlinePlayer(c.AccountId);
            if (target != null)
                ShowCode(target, c);

            await SendDiscordLog("Candidature acceptée", $"La candidature de **{c.PlayerName}** a été acceptée par **{staff.FullName}**.", "#2ecc71", c);
            return true;
        }

        private void AskRefuseReason(Player staff, WlCandidature c)
        {
            Panel panel = PanelHelper.Create($"Refuser #{c.Id} - {c.PlayerName}", UIPanel.PanelType.Input, staff, () => AskRefuseReason(staff, c));
            panel.TextLines.Add("Raison du refus (visible par le joueur, optionnelle)");
            panel.SetInputPlaceholder("Ex. : réponses trop courtes");
            panel.PreviousButtonWithAction(Color("Refuser", Colors.Error), async () =>
            {
                c.Status = WlStatus.Refused;
                c.RefuseReason = panel.inputText?.Trim();
                c.ReviewedBy = staff.FullName;
                c.ReviewedAt = Nova.UnixTimeNow();
                if (!await c.Save()) return false;

                staff.Notify(Title, $"Candidature de {c.PlayerName} refusée.", NotificationManager.Type.Success, 5f);
                Player target = FindOnlinePlayer(c.AccountId);
                if (target != null)
                    ShowRefused(target, c);

                await SendDiscordLog("Candidature refusée",
                    $"La candidature de **{c.PlayerName}** a été refusée par **{staff.FullName}**." +
                    (string.IsNullOrEmpty(c.RefuseReason) ? "" : $"\nRaison : {c.RefuseReason}"), "#e74c3c", c);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> Revoke(Player staff, WlCandidature c)
        {
            c.Status = WlStatus.Revoked;
            c.ReviewedBy = staff.FullName;
            c.ReviewedAt = Nova.UnixTimeNow();
            if (!await c.Save()) return false;

            staff.Notify(Title, $"Whitelist de {c.PlayerName} révoquée.", NotificationManager.Type.Success, 5f);
            Player target = FindOnlinePlayer(c.AccountId);
            if (target != null)
            {
                Block(target, c);
                target.Notify(Title, "Votre whitelist a été révoquée par le staff.", NotificationManager.Type.Error, 10f);
                await StartQuestionnaire(target);
            }

            await SendDiscordLog("Whitelist révoquée", $"La whitelist de **{c.PlayerName}** a été révoquée par **{staff.FullName}**.", "#e67e22", c);
            return true;
        }

        private async Task<string> GenerateUniqueCode()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            int length = Mathf.Clamp(Config.CodeLength, 4, 16);
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                while (true)
                {
                    byte[] bytes = new byte[length];
                    rng.GetBytes(bytes);
                    string code = Config.CodePrefix + new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
                    if ((await WlCandidature.Query(c => c.Code == code)).Count == 0)
                        return code;
                }
            }
        }

        // ------------------------------------------------------------------
        //  Joueurs connectés : whitelist manuelle / révocation
        // ------------------------------------------------------------------

        private async Task ShowOnlinePlayers(Player player)
        {
            Panel panel = PanelHelper.Create("Whitelist - Joueurs connectés", UIPanel.PanelType.Tab, player, () => _ = ShowOnlinePlayers(player));
            List<Player> players = Nova.server.Players.Where(IsOnline).ToList();

            foreach (Player target in players)
            {
                WlCandidature last = await GetLastCandidature(target.account.id);
                string status = last == null ? Color("Aucune candidature", Colors.Grey) : WlStatus.Label(last.Status);
                panel.AddTabLine($"{target.FullName} - {status}", _ => ShowPlayerActions(player, target, last));
            }
            if (players.Count == 0)
                panel.AddTabLine("Aucun joueur", _ => { });

            panel.NextButton("Choisir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void ShowPlayerActions(Player staff, Player target, WlCandidature last)
        {
            Panel panel = PanelHelper.Create($"Whitelist - {target.FullName}", UIPanel.PanelType.Tab, staff, () => ShowPlayerActions(staff, target, last));

            if (last == null || last.Status != WlStatus.Validated)
            {
                panel.AddTabLine(Color("Whitelister directement (sans code)", Colors.Success), async _ =>
                {
                    WlCandidature c = last != null && last.Status != WlStatus.Revoked && last.Status != WlStatus.Refused ? last : new WlCandidature
                    {
                        AccountId = target.account.id,
                        CharacterId = target.character?.Id ?? 0,
                        SteamId = target.steamId.ToString(),
                        SteamName = target.steamUsername,
                        PlayerName = target.FullName,
                        AnswersJson = "[]",
                        CreatedAt = Nova.UnixTimeNow()
                    };
                    c.Status = WlStatus.Validated;
                    c.ReviewedBy = staff.FullName;
                    c.ReviewedAt = c.ValidatedAt = Nova.UnixTimeNow();
                    if (!await c.Save()) return;

                    Unblock(target);
                    target.Notify(Title, "Vous avez été whitelisté par le staff. Bon jeu !", NotificationManager.Type.Success, 10f);
                    staff.Notify(Title, $"{target.FullName} est whitelisté.", NotificationManager.Type.Success, 5f);
                    await SendDiscordLog("Joueur whitelisté", $"**{target.FullName}** a été whitelisté manuellement par **{staff.FullName}**.", "#2ecc71", c);
                    panel.Previous();
                });
            }
            if (last != null)
                panel.AddTabLine("Voir sa candidature", _ => ShowCandidature(staff, last));
            if (last != null && (last.Status == WlStatus.Validated || last.Status == WlStatus.Accepted))
                panel.AddTabLine(Color("Révoquer la whitelist", Colors.Error), async _ => { if (await Revoke(staff, last)) panel.Previous(); });

            panel.NextButton("Valider", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Questions (questionnaire 100 % personnalisable)
        // ------------------------------------------------------------------

        public static async Task<List<WlQuestion>> GetQuestions()
        {
            return (await WlQuestion.QueryAll()).OrderBy(q => q.SortIndex).ThenBy(q => q.Id).ToList();
        }

        private async Task ShowQuestions(Player player)
        {
            List<WlQuestion> questions = await GetQuestions();
            string action = "";

            Panel panel = PanelHelper.Create("Whitelist - Questionnaire", UIPanel.PanelType.Tab, player, () => _ = ShowQuestions(player));
            if (questions.Count == 0)
                panel.AddTabLine("Aucune question, ajoutez-en une", _ => { });

            for (int i = 0; i < questions.Count; i++)
            {
                WlQuestion question = questions[i];
                int index = i;
                string type = question.IsChoice ? Color("[Choix]", Colors.Purple) : Color("[Texte]", Colors.Info);
                panel.AddTabLine($"{i + 1}. {type} {Truncate(question.Text, 60)}", async _ =>
                {
                    switch (action)
                    {
                        case "edit":
                            EditQuestion(player, question);
                            break;
                        case "up":
                        case "down":
                            int other = action == "up" ? index - 1 : index + 1;
                            if (other < 0 || other >= questions.Count) return;
                            await SwapOrder(questions, index, other);
                            panel.Refresh();
                            break;
                        case "delete":
                            await question.Delete();
                            player.Notify(Title, "Question supprimée.", NotificationManager.Type.Success, 5f);
                            panel.Refresh();
                            break;
                    }
                });
            }

            panel.NextButton(Color("Ajouter", Colors.Success), () => AddQuestion(player, questions.Count));
            if (questions.Count > 0)
            {
                panel.AddButton("Modifier", _ => { action = "edit"; panel.SelectTab(); });
                panel.AddButton("Monter", _ => { action = "up"; panel.SelectTab(); });
                panel.AddButton("Descendre", _ => { action = "down"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async Task SwapOrder(List<WlQuestion> questions, int a, int b)
        {
            // Renumérote tout pour garantir un ordre propre, puis échange les deux
            for (int i = 0; i < questions.Count; i++) questions[i].SortIndex = i;
            questions[a].SortIndex = b;
            questions[b].SortIndex = a;
            foreach (WlQuestion q in questions) await q.Save();
        }

        private void AddQuestion(Player player, int count)
        {
            AskText(player, "Nouvelle question", "Intitulé de la question", "Ex. : Quel âge as-tu ?", "", async text =>
            {
                WlQuestion question = new WlQuestion { Text = text, SortIndex = count, Required = true, Type = WlQuestion.TypeText };
                if (!await question.Save()) return false;
                player.Notify(Title, "Question ajoutée. Sélectionnez-la puis « Modifier » pour la personnaliser.", NotificationManager.Type.Success, 6f);
                return true;
            });
        }

        private void EditQuestion(Player player, WlQuestion question)
        {
            Panel panel = PanelHelper.Create("Whitelist - Modifier la question", UIPanel.PanelType.Tab, player, () => EditQuestion(player, question));

            panel.AddTabLine($"Intitulé : {Truncate(question.Text, 50)}", _ =>
                AskText(player, "Intitulé", "Nouvel intitulé de la question", question.Text, question.Text, async text =>
                {
                    question.Text = text;
                    return await question.Save();
                }));

            panel.AddTabLine($"Type : {(question.IsChoice ? "Choix multiple" : "Réponse libre")}", async _ =>
            {
                question.Type = question.IsChoice ? WlQuestion.TypeText : WlQuestion.TypeChoice;
                await question.Save();
                panel.Refresh();
            });

            if (question.IsChoice)
            {
                string choices = question.GetChoices().Count == 0 ? Color("aucun", Colors.Error) : string.Join(" / ", question.GetChoices());
                panel.AddTabLine($"Choix proposés : {Truncate(choices, 50)}", _ =>
                    AskText(player, "Choix proposés", "Séparez les choix par ;", "Oui;Non;Je ne sais pas", question.Choices ?? "", async text =>
                    {
                        question.Choices = text;
                        return await question.Save();
                    }));
            }
            else
            {
                panel.AddTabLine($"Longueur minimale : {question.MinLength} caractère(s)", _ =>
                    AskText(player, "Longueur minimale", "Nombre minimum de caractères (0 = aucun)", question.MinLength.ToString(), "", async text =>
                    {
                        if (!int.TryParse(text, out int value) || value < 0) return false;
                        question.MinLength = value;
                        return await question.Save();
                    }));
                panel.AddTabLine($"Texte d'aide : {(string.IsNullOrEmpty(question.Placeholder) ? "(défaut)" : question.Placeholder)}", _ =>
                    AskText(player, "Texte d'aide", "Texte grisé affiché dans la zone de réponse (- pour le défaut)", "Votre réponse...", question.Placeholder ?? "", async text =>
                    {
                        question.Placeholder = text == "-" ? "" : text;
                        return await question.Save();
                    }));
            }

            panel.AddTabLine($"Obligatoire : {YesNo(question.Required)}", async _ =>
            {
                question.Required = !question.Required;
                await question.Save();
                panel.Refresh();
            });

            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task CreateDefaultQuestions()
        {
            try
            {
                if ((await WlQuestion.QueryAll()).Count > 0) return;

                WlQuestion[] defaults =
                {
                    new WlQuestion { Text = "Quel âge as-tu (HRP) ?", Type = WlQuestion.TypeText, Required = true },
                    new WlQuestion { Text = "Quel sera le nom et prénom de ton personnage ?", Type = WlQuestion.TypeText, Required = true, MinLength = 3 },
                    new WlQuestion { Text = "As-tu déjà fait du RP ? Si oui, où ?", Type = WlQuestion.TypeText, Required = true },
                    new WlQuestion { Text = "Explique ce qu'est le FreeKill.", Type = WlQuestion.TypeText, Required = true, MinLength = 15 },
                    new WlQuestion { Text = "Explique ce qu'est le MetaGaming.", Type = WlQuestion.TypeText, Required = true, MinLength = 15 },
                    new WlQuestion { Text = "Raconte l'histoire de ton personnage.", Type = WlQuestion.TypeText, Required = true, MinLength = 50 },
                    new WlQuestion { Text = "As-tu lu et accepté le règlement du serveur ?", Type = WlQuestion.TypeChoice, Choices = "Oui;Non", Required = true }
                };
                for (int i = 0; i < defaults.Length; i++)
                {
                    defaults[i].SortIndex = i;
                    await defaults[i].Save();
                }
                Logger.LogSuccess(PluginInformations.SourceName, "Questionnaire par défaut créé (modifiable via /wladmin).");
            }
            catch (Exception e)
            {
                Logger.LogError(PluginInformations.SourceName, $"Création des questions par défaut impossible : {e.Message}");
            }
        }

        // ------------------------------------------------------------------
        //  Lieux d'attente
        // ------------------------------------------------------------------

        private async Task ShowWaitPoints(Player player)
        {
            List<WlWaitPoint> points = await WlWaitPoint.QueryAll();
            string action = "";

            Panel panel = PanelHelper.Create("Whitelist - Lieux d'attente", UIPanel.PanelType.Tab, player, () => _ = ShowWaitPoints(player));
            panel.TextLines.Add("Les joueurs non whitelistés sont envoyés sur un de ces lieux au hasard.");
            if (points.Count == 0)
                panel.AddTabLine("Aucun lieu, ajoutez votre position", _ => { });

            foreach (WlWaitPoint point in points)
            {
                panel.AddTabLine($"#{point.Id} {point.Name}", async _ =>
                {
                    switch (action)
                    {
                        case "tp":
                            player.setup.TargetSetPosition(point.GetPosition());
                            break;
                        case "move":
                            point.SetPosition(player.setup.transform.position);
                            if (await point.Save())
                                player.Notify(Title, "Lieu déplacé à votre position.", NotificationManager.Type.Success, 5f);
                            break;
                        case "delete":
                            await point.Delete();
                            player.Notify(Title, "Lieu supprimé.", NotificationManager.Type.Success, 5f);
                            panel.Refresh();
                            break;
                    }
                });
            }

            panel.NextButton(Color("Ajouter ma position", Colors.Success), () =>
                AskText(player, "Nouveau lieu d'attente", "Nom du lieu (votre position actuelle sera utilisée)", "Ex. : Hall d'arrivée", "", async name =>
                {
                    WlWaitPoint point = new WlWaitPoint { Name = name };
                    point.SetPosition(player.setup.transform.position);
                    return await point.Save();
                }));
            if (points.Count > 0)
            {
                panel.AddButton("Se téléporter", _ => { action = "tp"; panel.SelectTab(); });
                panel.AddButton("Déplacer ici", _ => { action = "move"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Paramètres
        // ------------------------------------------------------------------

        private void ShowSettings(Player player)
        {
            WlConfig c = Config;
            Panel panel = PanelHelper.Create("Whitelist - Paramètres", UIPanel.PanelType.Tab, player, () => ShowSettings(player));

            AddToggle(panel, "Whitelist activée", c.Enabled, v => c.Enabled = v);
            AddSetting(panel, player, "Webhook Discord", string.IsNullOrEmpty(c.DiscordWebhookUrl) ? Color("non défini", Colors.Error) : Color("défini", Colors.Success),
                "URL du webhook Discord (- pour désactiver)", c.DiscordWebhookUrl, text =>
                {
                    if (text == "-") { c.DiscordWebhookUrl = ""; return true; }
                    if (!InputUtils.IsValidDiscordWebhook(text)) return false;
                    c.DiscordWebhookUrl = text;
                    return true;
                });
            AddSetting(panel, player, "Mention Discord", string.IsNullOrEmpty(c.DiscordMention) ? "aucune" : c.DiscordMention,
                "Ex. : @here ou <@&ID_DU_ROLE> (- pour aucune)", c.DiscordMention, text => { c.DiscordMention = text == "-" ? "" : text; return true; });
            AddToggle(panel, "Logs Discord des décisions", c.DiscordLogDecisions, v => c.DiscordLogDecisions = v);
            AddSetting(panel, player, "Titre du questionnaire", c.FormTitle, "Titre affiché au joueur", c.FormTitle, text => { c.FormTitle = text; return true; });
            AddSetting(panel, player, "Message d'accueil", Truncate(c.WelcomeText, 30), "Message avant les questions (\\n = retour à la ligne)", c.WelcomeText, text => { c.WelcomeText = text; return true; });
            AddSetting(panel, player, "Message de fin", Truncate(c.EndText, 30), "Message après l'envoi", c.EndText, text => { c.EndText = text; return true; });
            AddSetting(panel, player, "Message d'acceptation", Truncate(c.AcceptedText, 30), "Message affiché avec le code (\\n = retour à la ligne)", c.AcceptedText, text => { c.AcceptedText = text; return true; });
            AddToggle(panel, "Geler les joueurs en attente", c.FreezePlayers, v => c.FreezePlayers = v);
            AddSetting(panel, player, "Distance max. autour du lieu", $"{c.MaxDistance} m", "Distance en mètres", c.MaxDistance.ToString(CultureInfo.InvariantCulture), text =>
            {
                if (!float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) || v < 1) return false;
                c.MaxDistance = v;
                return true;
            });
            AddToggle(panel, "Staff exempté", c.StaffBypass, v => c.StaffBypass = v);
            AddToggle(panel, "Nouvelle candidature après refus", c.AllowRetryAfterRefuse, v => c.AllowRetryAfterRefuse = v);
            AddSetting(panel, player, "Préfixe du code", string.IsNullOrEmpty(c.CodePrefix) ? "aucun" : c.CodePrefix, "Préfixe des codes (- pour aucun)", c.CodePrefix, text => { c.CodePrefix = text == "-" ? "" : text; return true; });
            AddSetting(panel, player, "Longueur du code", c.CodeLength.ToString(), "Nombre de caractères (4 à 16)", c.CodeLength.ToString(), text =>
            {
                if (!int.TryParse(text, out int v) || v < 4 || v > 16) return false;
                c.CodeLength = v;
                return true;
            });
            AddSetting(panel, player, "Niveau admin minimum", c.MinAdminLevel.ToString(), "Niveau admin requis pour gérer la whitelist", c.MinAdminLevel.ToString(), text =>
            {
                if (!int.TryParse(text, out int v) || v < 0) return false;
                c.MinAdminLevel = v;
                return true;
            });
            panel.AddTabLine("Recharger config.json", _ =>
            {
                LoadConfig();
                player.Notify(Title, "Configuration rechargée.", NotificationManager.Type.Success, 5f);
                panel.Refresh();
            });

            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AddToggle(Panel panel, string name, bool value, Action<bool> set)
        {
            panel.AddTabLine($"{name} : {YesNo(value)}", _ =>
            {
                set(!value);
                SaveConfig();
                panel.Refresh();
            });
        }

        private void AddSetting(Panel panel, Player player, string name, string display, string prompt, string current, Func<string, bool> set)
        {
            panel.AddTabLine($"{name} : {display}", _ =>
                AskText(player, name, prompt, string.IsNullOrEmpty(current) ? "" : current, "", text =>
                {
                    if (!set(text))
                    {
                        player.Notify(Title, "Valeur invalide.", NotificationManager.Type.Error, 5f);
                        return Task.FromResult(false);
                    }
                    SaveConfig();
                    return Task.FromResult(true);
                }));
        }

        // ------------------------------------------------------------------
        //  Mode test : le staff fait tout le parcours d'un nouveau joueur
        // ------------------------------------------------------------------

        public bool IsTesting(Player player) => player?.account != null && testers.Contains(player.account.id);

        private async Task ShowTestMenu(Player player)
        {
            List<WlQuestion> questions = await GetQuestions();
            int waitPoints = (await WlWaitPoint.QueryAll()).Count;
            List<WlCodePattern> patterns = await WlCodePattern.QueryAll();
            int placedPoints = (await ModEntity<NPoint>.Query(p => p.TypeName == nameof(WlCodePattern))).Count;
            bool testing = IsTesting(player);

            // Liste à onglets (défilante) : chaque vérification ouvre le menu à corriger
            Panel panel = PanelHelper.Create("Whitelist - Mode test", UIPanel.PanelType.Tab, player, () => _ = ShowTestMenu(player));
            if (testing)
                panel.AddTabLine(Color("Arrêter le test", Colors.Error), _ => StopTest(player, panel));
            else
                panel.AddTabLine(Color("Démarrer le test", Colors.Success), ui => _ = StartTest(player, panel));
            panel.AddTabLine("Comment se déroule le test ?", _ => ShowText(player, "Déroulement du test",
                "1. Vous êtes bloqué sur un lieu d'attente.",
                "2. Remplissez et envoyez le questionnaire.",
                "3. /wladmin : acceptez votre candidature [TEST].",
                "4. Entrez le code sur le point bleu.",
                "5. Vous êtes téléporté à la destination."));
            panel.AddTabLine(Check(questions.Count > 0, $"{questions.Count} question(s)"), ui => _ = ShowQuestions(player));
            panel.AddTabLine(Check(waitPoints > 0, $"{waitPoints} lieu(x) d'attente"), ui => _ = ShowWaitPoints(player));
            panel.AddTabLine(Check(patterns.Any(p => p.HasDestination), $"{patterns.Count(p => p.HasDestination)} point(s) avec destination"),
                _ => new WlCodePattern(false) { Context = this }.CreateOrGenerate(player));
            panel.AddTabLine(Check(placedPoints > 0, $"{placedPoints} point(s) de code placé(s)"),
                _ => new WlCodePattern(false) { Context = this }.CreateOrGenerate(player));
            bool hasWebhook = !string.IsNullOrEmpty(Config.DiscordWebhookUrl);
            panel.AddTabLine(Check(hasWebhook, hasWebhook ? "Webhook Discord (tester)" : "Webhook Discord (optionnel)"), ui =>
            {
                if (hasWebhook) _ = TestDiscord(player);
                else ShowSettings(player);
            });

            panel.NextButton("Choisir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static string Check(bool ok, string text) => (ok ? Color("[OK] ", Colors.Success) : Color("[X] ", Colors.Error)) + text;

        private async Task StartTest(Player player, Panel panel)
        {
            if (!IsStaff(player)) return;
            testers.Add(player.account.id);
            drafts.Remove(player.account.id);
            codeAttempts.Remove(player.account.id);
            panel.Close();

            Block(player, null);
            player.Notify(Title, "[TEST] Mode test démarré. Arrêt : /wladmin > Mode test > Arrêter le test.", NotificationManager.Type.Info, 10f);
            await StartQuestionnaire(player);
        }

        private void StopTest(Player player, Panel panel)
        {
            Unblock(player);
            panel.Close();
            player.Notify(Title, "[TEST] Mode test arrêté, vous êtes libre.", NotificationManager.Type.Success, 6f);
        }

        private async Task TestDiscord(Player player)
        {
            await SendDiscord("[TEST] Whitelist.nova", $"Message de test envoyé par **{player.FullName}**. Le webhook fonctionne !", Config.DiscordColor,
                new List<string>(), new List<string>(), false);
            player.Notify(Title, "Message de test envoyé sur Discord.", NotificationManager.Type.Success, 5f);
        }

        // ==================================================================
        //  Discord et alertes staff
        // ==================================================================

        public void NotifyStaff(string message)
        {
            foreach (Player staff in Nova.server.Players.Where(p => IsOnline(p) && IsStaff(p)).ToList())
            {
                staff.Notify(Title, message, NotificationManager.Type.Info, 10f);
                staff.SendText($"<color=#3498db>[Whitelist]</color> {message}");
            }
        }

        private async Task SendDiscordCandidature(WlCandidature c)
        {
            List<WlAnswer> answers = c.GetAnswers().Take(24).ToList();
            await SendDiscord((c.PlayerName ?? "").StartsWith(TestTag) ? "[TEST] Nouvelle candidature whitelist" : "Nouvelle candidature whitelist",
                $"**Joueur :** {c.PlayerName}\n**Steam :** {c.SteamName} ({c.SteamId})\n**Compte :** #{c.AccountId}\n**Candidature :** #{c.Id}\n\nÀ traiter en jeu : `/wladmin` ou AAMenu > Whitelist.",
                Config.DiscordColor,
                answers.Select(a => Truncate(a.Question, 250)).ToList(),
                answers.Select(a => string.IsNullOrEmpty(a.Answer) ? "(sans réponse)" : Truncate(a.Answer, 1000)).ToList(),
                true);
        }

        private async Task SendDiscordLog(string title, string description, string color, WlCandidature c)
        {
            if (!Config.DiscordLogDecisions) return;
            await SendDiscord(title, description, color,
                new List<string> { "Candidature", "Steam" },
                new List<string> { $"#{c.Id}", $"{c.SteamName} ({c.SteamId})" },
                false);
        }

        private async Task SendDiscord(string title, string description, string color, List<string> names, List<string> values, bool mention)
        {
            if (string.IsNullOrEmpty(Config.DiscordWebhookUrl)) return;
            try
            {
                DiscordWebhookClient client = new DiscordWebhookClient(Config.DiscordWebhookUrl);
                if (mention && !string.IsNullOrEmpty(Config.DiscordMention))
                    await DiscordHelper.SendMsg(client, Config.DiscordMention);
                await DiscordHelper.SendEmbed(client, color, title, description, names, values, false, true, "Whitelist.nova - by Loris Strange & Matheo Mercier");
            }
            catch (Exception e)
            {
                Logger.LogError(PluginInformations.SourceName, $"Envoi Discord impossible : {e.Message}");
            }
        }

        // ==================================================================
        //  Utilitaires
        // ==================================================================

        public static async Task<WlCandidature> GetLastCandidature(int accountId)
        {
            List<WlCandidature> list = await WlCandidature.Query(c => c.AccountId == accountId);
            return list.OrderByDescending(c => c.Id).FirstOrDefault();
        }

        private static bool IsOnline(Player player)
        {
            return player != null && player.setup != null && player.account != null;
        }

        private static Player FindOnlinePlayer(int accountId)
        {
            return Nova.server.Players.FirstOrDefault(p => IsOnline(p) && p.account.id == accountId);
        }

        /// <summary>Panneau de saisie générique : revient au menu précédent si la valeur est acceptée.</summary>
        public void AskText(Player player, string title, string prompt, string placeholder, string current, Func<string, Task<bool>> onValidate)
        {
            Panel panel = PanelHelper.Create($"Whitelist - {title}", UIPanel.PanelType.Input, player, () => AskText(player, title, prompt, placeholder, current, onValidate));
            panel.TextLines.Add(prompt);
            if (!string.IsNullOrEmpty(current))
                panel.TextLines.Add(Color($"Actuel : {current}", Colors.Grey));
            panel.SetInputPlaceholder(placeholder);
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string text = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    player.Notify(Title, "Le champ ne peut pas être vide.", NotificationManager.Type.Error, 5f);
                    return false;
                }
                return await onValidate(text);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Petit panneau de texte en lecture seule (garder peu de lignes : il ne défile pas).</summary>
        private void ShowText(Player player, string title, params string[] lines)
        {
            Panel panel = PanelHelper.Create($"Whitelist - {title}", UIPanel.PanelType.Text, player, () => ShowText(player, title, lines));
            foreach (string line in lines)
                panel.TextLines.Add(line);
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static string YesNo(bool value) => value ? Color("Oui", Colors.Success) : Color("Non", Colors.Error);

        private static IEnumerable<string> SplitLines(string text)
        {
            return (text ?? "").Replace("\\n", "\n").Split('\n');
        }
    }

    // ======================================================================
    //  Configuration (Plugins/Whitelist.nova/config.json)
    // ======================================================================

    public class WlConfig
    {
        public bool Enabled = true;
        public string DiscordWebhookUrl = "";
        public string DiscordMention = "";
        public string DiscordColor = "#3498db";
        public bool DiscordLogDecisions = true;
        public string FormTitle = "Candidature Whitelist";
        public string WelcomeText = "Bienvenue sur le serveur !\nAvant de pouvoir jouer, réponds à ce petit questionnaire.\nUn membre du staff étudiera ta candidature.";
        public string EndText = "Merci ! Ta candidature a été envoyée au staff. Tu recevras ton code de validation dès qu'elle sera acceptée.";
        public string AcceptedText = "Ta candidature a été acceptée !\nRends-toi sur le point de validation et entre ton code.";
        public string CodePrefix = "WL-";
        public int CodeLength = 6;
        public int MaxCodeAttempts = 5;
        public bool FreezePlayers = true;
        public float MaxDistance = 30f;
        public float CheckIntervalSeconds = 5f;
        public float SpawnDelaySeconds = 3f;
        public bool StaffBypass = true;
        public bool AllowRetryAfterRefuse = true;
        public int MinAdminLevel = 1;
        public int HistorySize = 50;
    }

    // ======================================================================
    //  Base de données (ModKit ORM)
    // ======================================================================

    public static class WlStatus
    {
        public const int Pending = 0;
        public const int Accepted = 1;
        public const int Refused = 2;
        public const int Validated = 3;
        public const int Revoked = 4;

        public static string Label(int status)
        {
            switch (status)
            {
                case Pending: return Color("En attente", Colors.Warning);
                case Accepted: return Color("Acceptée (code non saisi)", Colors.Info);
                case Refused: return Color("Refusée", Colors.Error);
                case Validated: return Color("Whitelisté", Colors.Success);
                case Revoked: return Color("Révoquée", Colors.Orange);
                default: return "?";
            }
        }
    }

    public class WlAnswer
    {
        public string Question;
        public string Answer;
    }

    public class WlQuestion : ModEntity<WlQuestion>
    {
        public const int TypeText = 0;
        public const int TypeChoice = 1;

        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }
        public int SortIndex { get; set; }
        public string Text { get; set; }
        public int Type { get; set; }
        /// <summary>Choix séparés par « ; » (questions à choix multiple).</summary>
        public string Choices { get; set; }
        public bool Required { get; set; } = true;
        public int MinLength { get; set; }
        public string Placeholder { get; set; }

        [Ignore]
        public bool IsChoice => Type == TypeChoice && GetChoices().Count > 0;

        public List<string> GetChoices()
        {
            return (Choices ?? "").Split(';').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        }
    }

    public class WlCandidature : ModEntity<WlCandidature>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }
        public int AccountId { get; set; }
        public int CharacterId { get; set; }
        public string SteamId { get; set; }
        public string SteamName { get; set; }
        public string PlayerName { get; set; }
        /// <summary>Questions et réponses (copie au moment de l'envoi).</summary>
        public string AnswersJson { get; set; }
        public int Status { get; set; }
        public string Code { get; set; }
        public string RefuseReason { get; set; }
        public string ReviewedBy { get; set; }
        public long CreatedAt { get; set; }
        public long ReviewedAt { get; set; }
        public long ValidatedAt { get; set; }

        public List<WlAnswer> GetAnswers()
        {
            try
            {
                return JsonConvert.DeserializeObject<List<WlAnswer>>(AnswersJson ?? "[]") ?? new List<WlAnswer>();
            }
            catch
            {
                return new List<WlAnswer>();
            }
        }

        public void SetAnswers(List<WlAnswer> answers)
        {
            AnswersJson = JsonConvert.SerializeObject(answers);
        }
    }

    public class WlWaitPoint : ModEntity<WlWaitPoint>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }
        public string Name { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public Vector3 GetPosition() => new Vector3(X, Y, Z);

        public void SetPosition(Vector3 position)
        {
            X = position.x;
            Y = position.y;
            Z = position.z;
        }
    }

    // ======================================================================
    //  Point bleu « Whitelist - Saisie du code »
    // ======================================================================

    /// <summary>
    /// Modèle de point de saisie du code. Chaque modèle a sa propre destination :
    /// le joueur qui entre un code valide y est téléporté.
    /// </summary>
    public class WlCodePattern : ModEntity<WlCodePattern>, PatternData
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }
        public string PatternName { get; set; }
        public bool HasDestination { get; set; }
        public float DestX { get; set; }
        public float DestY { get; set; }
        public float DestZ { get; set; }

        [Ignore]
        public string TypeName { get; set; } = nameof(WlCodePattern);

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        private static WhitelistPlugin Plugin => WhitelistPlugin.Instance;

        public WlCodePattern() { }

        public WlCodePattern(bool isCreated)
        {
            TypeName = nameof(WlCodePattern);
        }

        public Vector3? GetDestination() => HasDestination ? new Vector3(DestX, DestY, DestZ) : (Vector3?)null;

        public void SetDestination(Vector3 position)
        {
            HasDestination = true;
            DestX = position.x;
            DestY = position.y;
            DestZ = position.z;
        }

        public void OnPlayerTrigger(Player player)
        {
            _ = Plugin.OpenCodeInput(player, this);
        }

        public async Task SetProperties(int id)
        {
            WlCodePattern result = await Query(id);
            Id = id;
            TypeName = nameof(WlCodePattern);
            PatternName = result?.PatternName;
            HasDestination = result?.HasDestination ?? false;
            DestX = result?.DestX ?? 0;
            DestY = result?.DestY ?? 0;
            DestZ = result?.DestZ ?? 0;
        }

        /// <summary>Menu principal : choisir un modèle et placer un point à sa position.</summary>
        public async void CreateOrGenerate(Player player)
        {
            if (!Plugin.IsStaff(player)) return;

            List<WlCodePattern> patterns = await QueryAll();
            Panel panel = Plugin.PanelHelper.Create("Whitelist - Points de saisie du code", UIPanel.PanelType.Tab, player, () => CreateOrGenerate(player));
            panel.TextLines.Add("Placez le point près des lieux d'attente : les joueurs acceptés doivent pouvoir y aller.");

            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle, créez-en un", _ => { });
            foreach (WlCodePattern pattern in patterns)
            {
                string destination = pattern.HasDestination ? "" : Color(" (sans destination)", Colors.Error);
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}{destination}", async _ =>
                {
                    pattern.TypeName = nameof(WlCodePattern);
                    pattern.Context = Plugin;
                    if (await Plugin.PointHelper.CreateNPoint(player, pattern))
                        player.Notify(WhitelistPlugin.Title, $"Point « {pattern.PatternName} » placé à votre position.", NotificationManager.Type.Success, 5f);
                    else
                        player.Notify(WhitelistPlugin.Title, "Erreur lors de la création du point.", NotificationManager.Type.Error, 5f);
                });
            }

            if (patterns.Count > 0)
                panel.NextButton("Placer ici", () => panel.SelectTab());
            panel.NextButton("Nouveau modèle", () => SetPatternData(player));
            panel.NextButton("Modèles", async () => await GetPatternData(player, true));
            panel.NextButton("Points", async () => await GetNPoints(player));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Création d'un modèle : la destination est la position actuelle du staff.</summary>
        public void SetPatternData(Player player)
        {
            Plugin.AskText(player, "Nouveau point de code", "Nom du point. Votre position actuelle sera la destination des joueurs validés.", "Validation whitelist", "", async name =>
            {
                WlCodePattern pattern = new WlCodePattern(false) { PatternName = name };
                pattern.SetDestination(player.setup.transform.position);
                if (!await pattern.Save()) return false;
                player.Notify(WhitelistPlugin.Title, $"Modèle « {name} » créé avec votre position comme destination. Allez où placer le point puis « Placer ici ».", NotificationManager.Type.Success, 8f);
                return true;
            });
        }

        /// <summary>Liste des modèles : renommer, changer la destination ou supprimer.</summary>
        public async Task GetPatternData(Player player, bool forEdit)
        {
            List<WlCodePattern> patterns = await QueryAll();
            string action = "";

            Panel panel = Plugin.PanelHelper.Create("Whitelist - Modèles de point", UIPanel.PanelType.Tab, player, async () => await GetPatternData(player, forEdit));
            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle", _ => { });

            foreach (WlCodePattern pattern in patterns)
            {
                string destination = pattern.HasDestination ? "" : Color(" (sans destination)", Colors.Error);
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}{destination}", async _ =>
                {
                    pattern.TypeName = nameof(WlCodePattern);
                    pattern.Context = Plugin;
                    switch (action)
                    {
                        case "rename":
                            Plugin.AskText(player, "Renommer", $"Nouveau nom pour « {pattern.PatternName} »", pattern.PatternName ?? "", "", async name =>
                            {
                                pattern.PatternName = name;
                                return await pattern.Save();
                            });
                            break;
                        case "destination":
                            pattern.SetDestination(player.setup.transform.position);
                            if (await pattern.Save())
                                player.Notify(WhitelistPlugin.Title, "Destination définie sur votre position.", NotificationManager.Type.Success, 5f);
                            panel.Refresh();
                            break;
                        case "tp":
                            Vector3? target = pattern.GetDestination();
                            if (target != null) player.setup.TargetSetPosition(target.Value);
                            break;
                        case "delete":
                            await Plugin.PointHelper.DeleteNPointsByPattern(player, pattern);
                            if (await pattern.Delete())
                                player.Notify(WhitelistPlugin.Title, $"Modèle « {pattern.PatternName} » et ses points supprimés.", NotificationManager.Type.Success, 5f);
                            panel.Refresh();
                            break;
                    }
                });
            }

            if (patterns.Count > 0 && forEdit)
            {
                panel.AddButton("Renommer", _ => { action = "rename"; panel.SelectTab(); });
                panel.AddButton("Destination ici", _ => { action = "destination"; panel.SelectTab(); });
                panel.AddButton("TP destination", _ => { action = "tp"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Liste de tous les points placés : se téléporter, déplacer ou supprimer.</summary>
        public async Task GetNPoints(Player player)
        {
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == nameof(WlCodePattern));
            Dictionary<int, string> names = (await QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);
            string action = "";

            Panel panel = Plugin.PanelHelper.Create("Whitelist - Points placés", UIPanel.PanelType.Tab, player, async () => await GetNPoints(player));
            if (points.Count == 0)
                panel.AddTabLine("Aucun point placé", _ => { });

            foreach (NPoint point in points)
            {
                string name = names.TryGetValue(point.PatternId, out string n) ? n : "?";
                panel.AddTabLine($"Point #{point.Id} - {name}", async _ =>
                {
                    switch (action)
                    {
                        case "tp":
                            Plugin.PointHelper.PlayerSetPositionToNPoint(player, point);
                            break;
                        case "move":
                            if (await Plugin.PointHelper.SetNPointPosition(player, point))
                                player.Notify(WhitelistPlugin.Title, "Point déplacé à votre position.", NotificationManager.Type.Success, 5f);
                            break;
                        case "delete":
                            await Plugin.PointHelper.DeleteNPoint(point);
                            player.Notify(WhitelistPlugin.Title, $"Point #{point.Id} supprimé.", NotificationManager.Type.Success, 5f);
                            panel.Refresh();
                            break;
                    }
                });
            }

            if (points.Count > 0)
            {
                panel.AddButton("Se téléporter", _ => { action = "tp"; panel.SelectTab(); });
                panel.AddButton("Déplacer ici", _ => { action = "move"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }
}
