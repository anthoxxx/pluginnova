using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.DB;
using Life.Network;
using Life.UI;
using Mirror;
using ModKit.Helper;
using ModKit.Helper.PointHelper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.ORM;
using ModKit.Utils;
using Newtonsoft.Json;
using SQLite;
using static ModKit.Helper.TextFormattingHelper;

namespace AutoEcole
{
    /// <summary>
    /// Auto-École By Loris Strange : le staff place des points bleus « auto-école »
    /// où les joueurs s'inscrivent, passent l'examen du code (questions configurables)
    /// et obtiennent leur permis avec un capital de points.
    /// </summary>
    public class AutoEcolePlugin : ModKit.ModKit
    {
        public const string NotifTitle = "Auto-École";

        /// <summary>Nombre maximum de questions par examen, quelle que soit la configuration.</summary>
        public const int MaxQuestions = 10;

        public static AutoEcolePlugin Instance { get; private set; }

        public AutoEcoleConfig Config { get; private set; } = AutoEcoleConfig.CreateDefault();

        /// <summary>Examens en cours, par identifiant de personnage.</summary>
        private readonly Dictionary<int, ExamSession> _exams = new Dictionary<int, ExamSession>();

        private readonly Random _random = new Random();

        private string ConfigPath => Path.Combine(PluginPath, "AutoEcole", "config.json");

        public AutoEcolePlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "Loris Strange");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            Instance = this;

            LoadConfig();

            Orm.RegisterTable<AutoEcolePattern>();
            Orm.RegisterTable<AutoEcoleLicense>();

            AutoEcolePattern pattern = new AutoEcolePattern(false);
            PointHelper.AddPattern(nameof(AutoEcolePattern), pattern);

            if (AAMenu.AAMenu.menu != null)
            {
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, "Auto-École", pattern, this);
                AAMenu.Menu.AddDocumentTabLine(PluginInformations, "Mes permis de conduire", ui =>
                {
                    Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                    player.ClosePanel(ui);
                    ShowLicenses(player, player);
                });
                AAMenu.Menu.AddInteractionTabLine(PluginInformations, "Montrer mes permis", ui =>
                {
                    Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                    player.ClosePanel(ui);
                    ShowLicensesToClosest(player);
                });
                AAMenu.Menu.AddInteractionTabLine(PluginInformations, "Contrôler un permis (police)", ui =>
                {
                    Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                    player.ClosePanel(ui);
                    ControlClosest(player);
                });
                AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, 1, "Auto-École", ui =>
                {
                    Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                    player.ClosePanel(ui);
                    AdminMenu(player);
                });
            }
            else
            {
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : utilisez les commandes /autoecole, /permis et /controlepermis.");
            }

            RegisterCommands();

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", "initialisé");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            PointHelper.InitAllNPoint(player);
            SyncOnSpawn(player);
        }

        public override void OnPlayerDisconnect(NetworkConnection conn)
        {
            // Quitter le serveur pendant l'examen compte comme un échec
            ExamSession session = _exams.Values.FirstOrDefault(s => s.Player.conn == conn);
            if (session != null)
                FinishExam(session, true, false);

            base.OnPlayerDisconnect(conn);
        }

        // ------------------------------------------------------------------
        //  Configuration
        // ------------------------------------------------------------------

        public bool LoadConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));

                if (!File.Exists(ConfigPath))
                {
                    Config = AutoEcoleConfig.CreateDefault();
                    File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(Config, Formatting.Indented));
                    Logger.LogSuccess(PluginInformations.SourceName, $"Configuration par défaut créée : {ConfigPath}");
                }
                else
                {
                    AutoEcoleConfig loaded = JsonConvert.DeserializeObject<AutoEcoleConfig>(File.ReadAllText(ConfigPath));
                    if (loaded == null)
                        throw new Exception("fichier vide");
                    Config = loaded;
                }

                Config.Validate(PluginInformations.SourceName);
                return true;
            }
            catch (Exception ex)
            {
                // On ne réécrit pas le fichier : l'erreur de l'admin ne doit pas effacer ses questions
                Logger.LogError(PluginInformations.SourceName, $"config.json invalide, configuration précédente conservée : {ex.Message}");
                return false;
            }
        }

        private int QuestionCount(LicenseConfig license)
        {
            int wanted = Math.Max(1, Math.Min(Config.QuestionsParExamen, MaxQuestions));
            return Math.Min(wanted, license.Questions.Count);
        }

        private int RequiredCount(int questionCount)
        {
            return Math.Max(1, Math.Min(Config.BonnesReponsesRequises, questionCount));
        }

        private long RetryDelaySeconds => Math.Max(0, Config.DelaiApresEchecMinutes) * 60L;

        private long RetryRemaining(AutoEcoleLicense record)
        {
            if (record.LastFailAt <= 0) return 0;
            return Math.Max(0, record.LastFailAt + RetryDelaySeconds - DateUtils.GetCurrentTime());
        }

        // ------------------------------------------------------------------
        //  Commandes
        // ------------------------------------------------------------------

        private void RegisterCommands()
        {
            new SChatCommand("/permis", "Afficher vos permis de conduire", "/permis",
                (Action<Player, string[]>)((player, args) => ShowLicenses(player, player))).Register();

            new SChatCommand("/controlepermis", "Contrôler le permis du joueur le plus proche (police)", "/controlepermis",
                (Action<Player, string[]>)((player, args) => ControlClosest(player))).Register();

            new SChatCommand("/autoecole", "Gérer l'auto-école (staff)", "/autoecole",
                (Action<Player, string[]>)((player, args) => AdminMenu(player))).Register();
        }

        // ------------------------------------------------------------------
        //  Menu principal de l'auto-école (point bleu)
        // ------------------------------------------------------------------

        public async void OpenSchool(Player player, string schoolName)
        {
            // Un examen en cours reprend là où il s'était arrêté
            if (_exams.TryGetValue(player.character.Id, out ExamSession current))
            {
                ShowQuestion(current);
                return;
            }

            string title = string.IsNullOrWhiteSpace(schoolName) ? Config.NomAutoEcole : schoolName;
            List<AutoEcoleLicense> records = await AutoEcoleLicense.GetAll(player.character.Id);

            Panel panel = PanelHelper.Create(title, UIPanel.PanelType.Tab, player, () => OpenSchool(player, schoolName));

            foreach (LicenseConfig license in Config.Permis)
            {
                AutoEcoleLicense record = records.FirstOrDefault(r => r.Category == license.Code)
                                          ?? AutoEcoleLicense.New(player.character.Id, license.Code);
                panel.AddTabLine($"{license.Nom} - {SchoolStatus(license, record)}", async _ => await SelectLicense(player, license, panel));
            }

            if (Config.StageRecuperation.Actif)
                panel.AddTabLine($"Stage de récupération de points - {Price(Config.StageRecuperation.Prix)}", _ => RecoveryMenu(player));

            panel.AddTabLine(Color("Mes permis", Colors.Info), _ => ShowLicenses(player, player));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        private string SchoolStatus(LicenseConfig license, AutoEcoleLicense record)
        {
            if (record.IsValid)
                return Color($"Obtenu ({record.Points}/{license.PointsMax} pts)", Colors.Success);

            long remaining = RetryRemaining(record);
            if (remaining > 0)
                return Color($"Réessayer dans {DateUtils.FormatTime((int)remaining)}", Colors.Warning);

            if (record.Registered)
                return Color("Inscrit : passer l'examen", Colors.Info);

            return Price(license.Prix);
        }

        private async Task SelectLicense(Player player, LicenseConfig license, Panel from)
        {
            AutoEcoleLicense record = await AutoEcoleLicense.Get(player.character.Id, license.Code);

            if (record.IsValid)
            {
                player.Notify(NotifTitle, $"Vous avez déjà le {license.Nom} ({record.Points}/{license.PointsMax} points).", NotificationManager.Type.Info, 5f);
                from.Refresh();
                return;
            }

            if (!string.IsNullOrEmpty(license.PermisRequis))
            {
                AutoEcoleLicense required = await AutoEcoleLicense.Get(player.character.Id, license.PermisRequis);
                if (!required.IsValid)
                {
                    string requiredName = Config.Permis.FirstOrDefault(l => l.Code == license.PermisRequis)?.Nom ?? $"permis {license.PermisRequis}";
                    player.Notify(NotifTitle, $"Vous devez d'abord obtenir le {requiredName}.", NotificationManager.Type.Error, 5f);
                    from.Refresh();
                    return;
                }
            }

            if (license.Questions.Count == 0)
            {
                player.Notify(NotifTitle, "Aucune question n'est configurée pour ce permis. Prévenez le staff.", NotificationManager.Type.Error, 5f);
                from.Refresh();
                return;
            }

            long remaining = RetryRemaining(record);
            if (remaining > 0)
            {
                player.Notify(NotifTitle, $"Vous avez raté l'examen. Vous pourrez le repasser dans {DateUtils.FormatTime((int)remaining)}.", NotificationManager.Type.Warning, 6f);
                from.Refresh();
                return;
            }

            if (record.Registered)
                ShowExamIntro(player, license);
            else
                ShowRegistration(player, license);
        }

        // ------------------------------------------------------------------
        //  Inscription et paiement
        // ------------------------------------------------------------------

        private void ShowRegistration(Player player, LicenseConfig license)
        {
            int count = QuestionCount(license);

            Panel panel = PanelHelper.Create($"Inscription - {license.Nom}", UIPanel.PanelType.Text, player, () => ShowRegistration(player, license));
            panel.TextLines.Add(Bold($"Inscription au {license.Nom}"));
            if (!string.IsNullOrWhiteSpace(license.Description))
                panel.TextLines.Add(Color(license.Description, Colors.Grey));
            panel.TextLines.Add("");
            panel.TextLines.Add($"Prix : {Bold(Price(license.Prix))}");
            panel.TextLines.Add($"Examen du code : {count} questions, {RequiredCount(count)} bonnes réponses minimum.");
            panel.TextLines.Add($"Capital de points à l'obtention : {license.PointsMax} points.");
            panel.TextLines.Add(Config.PayerAChaqueTentative
                ? Color("Le prix est à payer à chaque tentative.", Colors.Warning)
                : Color($"En cas d'échec, vous repassez l'examen gratuitement après {Config.DelaiApresEchecMinutes} minutes.", Colors.Info));

            panel.NextButton("Payer en espèces", () => Register(player, license, false));
            panel.NextButton("Payer par carte", () => Register(player, license, true));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void Register(Player player, LicenseConfig license, bool bank)
        {
            AutoEcoleLicense record = await AutoEcoleLicense.Get(player.character.Id, license.Code);
            if (record.IsValid || record.Registered)
            {
                // Double clic ou retour en arrière : ne pas faire payer deux fois
                ShowExamIntro(player, license);
                return;
            }

            if (!TryPay(player, license.Prix, bank, $"{Config.NomAutoEcole} - {license.Nom}"))
            {
                ShowRegistration(player, license);
                return;
            }

            record.Registered = true;
            if (!await record.Save())
            {
                player.Notify(NotifTitle, "Erreur lors de l'enregistrement de l'inscription. Prévenez le staff.", NotificationManager.Type.Error, 6f);
                return;
            }

            player.Notify(NotifTitle, $"Inscription au {license.Nom} réglée ({Price(license.Prix)}). Bonne chance !", NotificationManager.Type.Success, 5f);
            ShowExamIntro(player, license);
        }

        private static bool TryPay(Player player, int price, bool bank, string reason)
        {
            if (price <= 0) return true;

            if (bank)
            {
                if (player.character.Bank < price)
                {
                    player.Notify(NotifTitle, $"Solde bancaire insuffisant ({Price(price)} requis).", NotificationManager.Type.Error, 5f);
                    return false;
                }
                player.AddBankMoney(-price, reason);
            }
            else
            {
                if (player.Money < price)
                {
                    player.Notify(NotifTitle, $"Vous n'avez pas assez d'argent sur vous ({Price(price)} requis).", NotificationManager.Type.Error, 5f);
                    return false;
                }
                player.AddMoney(-price, reason);
            }
            return true;
        }

        // ------------------------------------------------------------------
        //  Examen du code
        // ------------------------------------------------------------------

        private void ShowExamIntro(Player player, LicenseConfig license)
        {
            int count = QuestionCount(license);

            Panel panel = PanelHelper.Create($"Examen - {license.Nom}", UIPanel.PanelType.Text, player, () => ShowExamIntro(player, license));
            panel.TextLines.Add(Bold($"Examen du code - {license.Nom}"));
            panel.TextLines.Add("");
            panel.TextLines.Add($"- {count} questions, une seule bonne réponse par question.");
            panel.TextLines.Add($"- Il faut au moins {Bold($"{RequiredCount(count)}/{count}")} bonnes réponses pour réussir.");
            panel.TextLines.Add($"- En cas d'échec, vous devrez attendre {Config.DelaiApresEchecMinutes} minutes avant de le repasser.");
            panel.TextLines.Add("- Abandonner ou quitter le serveur pendant l'examen compte comme un échec.");
            panel.TextLines.Add("");
            panel.TextLines.Add(Color("Les résultats sont donnés à la fin de l'examen.", Colors.Grey));

            panel.NextButton("Commencer l'examen", () => StartExam(player, license));
            panel.CloseButton("Plus tard");
            panel.Display();
        }

        private async void StartExam(Player player, LicenseConfig license)
        {
            int characterId = player.character.Id;
            if (_exams.ContainsKey(characterId))
            {
                ShowQuestion(_exams[characterId]);
                return;
            }

            // On revérifie tout : le panneau a pu rester ouvert longtemps
            AutoEcoleLicense record = await AutoEcoleLicense.Get(characterId, license.Code);
            if (record.IsValid || !record.Registered || RetryRemaining(record) > 0 || license.Questions.Count == 0)
            {
                player.Notify(NotifTitle, "Vous ne pouvez pas passer cet examen pour le moment.", NotificationManager.Type.Error, 5f);
                return;
            }

            int count = QuestionCount(license);
            List<ExamQuestion> questions = license.Questions
                .OrderBy(_ => _random.Next())
                .Take(count)
                .Select(q => ExamQuestion.Shuffled(q, _random))
                .ToList();

            ExamSession session = new ExamSession(player, license, questions, RequiredCount(count));
            _exams[characterId] = session;
            ShowQuestion(session);
        }

        private void ShowQuestion(ExamSession session)
        {
            Player player = session.Player;
            int index = session.Index;
            ExamQuestion question = session.Questions[index];

            Panel panel = PanelHelper.Create($"{session.License.Nom} - Question {index + 1}/{session.Questions.Count}", UIPanel.PanelType.Text, player, () => ShowQuestion(session));
            panel.TextLines.Add(Bold(question.Text));
            panel.TextLines.Add("");
            for (int i = 0; i < question.Answers.Count; i++)
                panel.TextLines.Add($"{Bold(Letter(i))}. {question.Answers[i]}");

            for (int i = 0; i < question.Answers.Count; i++)
            {
                int choice = i;
                panel.NextButton(Letter(i), () => Answer(session, index, choice));
            }
            panel.NextButton(Color("Abandonner", Colors.Error), () => ConfirmAbandon(session));
            panel.Display();
        }

        private void Answer(ExamSession session, int questionIndex, int choice)
        {
            // Ignore les clics sur un ancien panneau ou un examen terminé
            if (!IsCurrent(session) || session.Index != questionIndex) return;

            session.Choices.Add(choice);
            session.Index++;

            if (session.Index >= session.Questions.Count)
                FinishExam(session, false, true);
            else
                ShowQuestion(session);
        }

        private void ConfirmAbandon(ExamSession session)
        {
            Panel panel = PanelHelper.Create($"{session.License.Nom} - Abandon", UIPanel.PanelType.Text, session.Player, () => ConfirmAbandon(session));
            panel.TextLines.Add("Voulez-vous vraiment abandonner l'examen ?");
            panel.TextLines.Add(Color($"Cela compte comme un échec : {Config.DelaiApresEchecMinutes} minutes d'attente avant de le repasser.", Colors.Warning));
            panel.NextButton("Reprendre", () => ShowQuestion(session));
            panel.NextButton(Color("Abandonner", Colors.Error), () => { if (IsCurrent(session)) FinishExam(session, true, true); });
            panel.Display();
        }

        private bool IsCurrent(ExamSession session)
        {
            return _exams.TryGetValue(session.CharacterId, out ExamSession current) && current == session;
        }

        private async void FinishExam(ExamSession session, bool abandoned, bool showResult)
        {
            _exams.Remove(session.CharacterId);

            int correct = session.CorrectCount;
            bool passed = !abandoned && correct >= session.Required;

            AutoEcoleLicense record = await AutoEcoleLicense.Get(session.CharacterId, session.License.Code);
            record.Attempts++;
            if (passed)
            {
                record.Obtained = true;
                record.Registered = false; // si le permis est annulé, il faudra se réinscrire
                record.Points = session.License.PointsMax;
                record.ObtainedAt = DateUtils.GetCurrentTime();
                record.LastFailAt = 0;
            }
            else
            {
                record.LastFailAt = DateUtils.GetCurrentTime();
                if (Config.PayerAChaqueTentative)
                    record.Registered = false;
            }

            if (!await record.Save())
                Logger.LogError(PluginInformations.SourceName, $"Impossible d'enregistrer le résultat d'examen du personnage {session.CharacterId}.");

            if (!showResult) return;

            if (passed)
                SyncNative(session.Player, session.License, record);

            ShowResult(session, correct, passed, abandoned);
        }

        private void ShowResult(ExamSession session, int correct, bool passed, bool abandoned)
        {
            Player player = session.Player;
            LicenseConfig license = session.License;
            int total = session.Questions.Count;

            Panel panel = PanelHelper.Create($"Résultat - {license.Nom}", UIPanel.PanelType.Text, player, () => ShowResult(session, correct, passed, abandoned));
            panel.TextLines.Add(Size(Bold(Color($"{correct}/{total}", passed ? Colors.Success : Colors.Error)), 40));
            panel.TextLines.Add("");

            if (passed)
            {
                panel.TextLines.Add(Bold(Color("Félicitations, vous avez réussi !", Colors.Success)));
                panel.TextLines.Add($"Vous obtenez le {license.Nom} avec {license.PointsMax} points.");
                panel.TextLines.Add(Color("Roulez prudemment !", Colors.Grey));
                player.Notify(NotifTitle, $"Vous avez obtenu le {license.Nom} ({correct}/{total}) !", NotificationManager.Type.Success, 8f);
            }
            else
            {
                panel.TextLines.Add(Bold(Color(abandoned ? "Examen abandonné." : "Examen raté.", Colors.Error)));
                panel.TextLines.Add($"Il fallait au moins {session.Required}/{total} bonnes réponses.");
                panel.TextLines.Add($"Vous pourrez le repasser dans {Config.DelaiApresEchecMinutes} minutes.");
                if (Config.PayerAChaqueTentative)
                    panel.TextLines.Add(Color($"Une nouvelle inscription ({Price(license.Prix)}) sera nécessaire.", Colors.Warning));
                player.Notify(NotifTitle, $"Examen raté ({correct}/{total}). Réessayez dans {Config.DelaiApresEchecMinutes} minutes.", NotificationManager.Type.Error, 8f);
            }

            if (Config.AfficherCorrection && session.Mistakes.Any())
                panel.NextButton("Voir mes erreurs", () => ShowCorrection(session));
            panel.CloseButton();
            panel.Display();
        }

        private void ShowCorrection(ExamSession session)
        {
            Panel panel = PanelHelper.Create($"Correction - {session.License.Nom}", UIPanel.PanelType.Tab, session.Player, () => ShowCorrection(session));

            foreach ((int number, ExamQuestion question, int choice) in session.Mistakes)
            {
                panel.AddTabLine($"Q{number}. {Truncate(question.Text, 60)}", _ => ShowCorrectionDetail(session, number, question, choice));
            }

            panel.NextButton("Voir", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        private void ShowCorrectionDetail(ExamSession session, int number, ExamQuestion question, int choice)
        {
            Panel panel = PanelHelper.Create($"Correction - Question {number}", UIPanel.PanelType.Text, session.Player, () => ShowCorrectionDetail(session, number, question, choice));
            panel.TextLines.Add(Bold(question.Text));
            panel.TextLines.Add("");
            panel.TextLines.Add($"Votre réponse : {Color(choice >= 0 ? question.Answers[choice] : "aucune", Colors.Error)}");
            panel.TextLines.Add($"Bonne réponse : {Color(question.Answers[question.Correct], Colors.Success)}");
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Stage de récupération de points
        // ------------------------------------------------------------------

        private async void RecoveryMenu(Player player)
        {
            List<AutoEcoleLicense> records = await AutoEcoleLicense.GetAll(player.character.Id);

            Panel panel = PanelHelper.Create("Stage de récupération de points", UIPanel.PanelType.Tab, player, () => RecoveryMenu(player));
            bool any = false;

            foreach (LicenseConfig license in Config.Permis)
            {
                AutoEcoleLicense record = records.FirstOrDefault(r => r.Category == license.Code);
                if (record == null || !record.IsValid) continue;
                any = true;

                panel.AddTabLine($"{license.Nom} - {record.Points}/{license.PointsMax} pts", _ => ShowRecoveryPayment(player, license));
            }

            if (!any)
                panel.AddTabLine("Aucun permis valide", _ => panel.Refresh());

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void ShowRecoveryPayment(Player player, LicenseConfig license)
        {
            StageConfig stage = Config.StageRecuperation;
            AutoEcoleLicense record = await AutoEcoleLicense.Get(player.character.Id, license.Code);

            if (!record.IsValid)
            {
                player.Notify(NotifTitle, "Ce permis n'est pas valide.", NotificationManager.Type.Error, 5f);
                return;
            }
            if (record.Points >= license.PointsMax)
            {
                player.Notify(NotifTitle, $"Votre {license.Nom} a déjà tous ses points.", NotificationManager.Type.Info, 5f);
                RecoveryMenu(player);
                return;
            }
            long remaining = record.LastCourseAt + Math.Max(0, stage.DelaiEntreStagesHeures) * 3600L - DateUtils.GetCurrentTime();
            if (remaining > 0)
            {
                player.Notify(NotifTitle, $"Prochain stage possible dans {DateUtils.FormatTime((int)remaining)}.", NotificationManager.Type.Warning, 6f);
                RecoveryMenu(player);
                return;
            }

            int gained = Math.Min(stage.PointsRecuperes, license.PointsMax - record.Points);

            Panel panel = PanelHelper.Create($"Stage - {license.Nom}", UIPanel.PanelType.Text, player, () => ShowRecoveryPayment(player, license));
            panel.TextLines.Add(Bold("Stage de sensibilisation à la sécurité routière"));
            panel.TextLines.Add("");
            panel.TextLines.Add($"Points actuels : {record.Points}/{license.PointsMax}");
            panel.TextLines.Add($"Points récupérés : {Color($"+{gained}", Colors.Success)}");
            panel.TextLines.Add($"Prix : {Bold(Price(stage.Prix))}");
            if (stage.DelaiEntreStagesHeures > 0)
                panel.TextLines.Add(Color($"Un seul stage toutes les {stage.DelaiEntreStagesHeures} heures.", Colors.Grey));

            panel.PreviousButtonWithAction("Payer en espèces", () => DoRecovery(player, license, false));
            panel.PreviousButtonWithAction("Payer par carte", () => DoRecovery(player, license, true));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> DoRecovery(Player player, LicenseConfig license, bool bank)
        {
            StageConfig stage = Config.StageRecuperation;
            AutoEcoleLicense record = await AutoEcoleLicense.Get(player.character.Id, license.Code);
            long remaining = record.LastCourseAt + Math.Max(0, stage.DelaiEntreStagesHeures) * 3600L - DateUtils.GetCurrentTime();
            if (!record.IsValid || record.Points >= license.PointsMax || remaining > 0)
                return true;

            if (!TryPay(player, stage.Prix, bank, $"{Config.NomAutoEcole} - Stage {license.Nom}"))
                return false;

            int before = record.Points;
            record.Points = Math.Min(license.PointsMax, record.Points + stage.PointsRecuperes);
            record.LastCourseAt = DateUtils.GetCurrentTime();
            await record.Save();
            SyncNative(player, license, record);

            player.Notify(NotifTitle, $"Stage effectué : +{record.Points - before} points sur votre {license.Nom} ({record.Points}/{license.PointsMax}).", NotificationManager.Type.Success, 6f);
            return true;
        }

        // ------------------------------------------------------------------
        //  Consultation des permis
        // ------------------------------------------------------------------

        public async void ShowLicenses(Player viewer, Player owner)
        {
            List<AutoEcoleLicense> records = await AutoEcoleLicense.GetAll(owner.character.Id);

            Panel panel = PanelHelper.Create($"Permis de conduire - {owner.FullName}", UIPanel.PanelType.Text, viewer, () => ShowLicenses(viewer, owner));
            panel.TextLines.Add(Bold($"{owner.FullName}"));
            panel.TextLines.Add(Color(Config.NomAutoEcole, Colors.Grey));

            foreach (LicenseConfig license in Config.Permis)
            {
                AutoEcoleLicense record = records.FirstOrDefault(r => r.Category == license.Code);
                panel.TextLines.Add("");
                panel.TextLines.Add(Bold(license.Nom));
                panel.TextLines.Add(LicenseStatus(license, record));
            }

            panel.CloseButton();
            panel.Display();
        }

        private static string LicenseStatus(LicenseConfig license, AutoEcoleLicense record)
        {
            if (record != null && record.IsValid)
            {
                Colors color = record.Points * 3 <= license.PointsMax ? Colors.Warning : Colors.Success;
                return $"{Color("Valide", Colors.Success)} - {Color($"{record.Points}/{license.PointsMax} points", color)} - obtenu le {DateUtils.FormatUnixTimestamp(record.ObtainedAt)}";
            }
            if (record != null && record.ObtainedAt > 0)
                return Color("Annulé (solde de points nul)", Colors.Error);
            return Color("Non obtenu", Colors.Grey);
        }

        private void ShowLicensesToClosest(Player player)
        {
            Player target = player.GetClosestPlayer();
            if (target == null)
            {
                player.Notify(NotifTitle, "Aucun joueur à proximité.", NotificationManager.Type.Warning, 5f);
                return;
            }
            ShowLicenses(target, player);
            player.Notify(NotifTitle, $"Vous montrez vos permis à {target.FullName}.", NotificationManager.Type.Info, 5f);
        }

        // ------------------------------------------------------------------
        //  Contrôle police : retrait de points
        // ------------------------------------------------------------------

        private static bool CanControl(Player player)
        {
            if (player.IsAdmin && player.serviceAdmin) return true;
            return player.serviceMetier && player.HasBiz && player.biz != null
                   && player.biz.IsActivity(Life.BizSystem.Activity.Type.LawEnforcement);
        }

        private void ControlClosest(Player player)
        {
            if (!CanControl(player))
            {
                player.Notify(NotifTitle, "Réservé aux forces de l'ordre en service.", NotificationManager.Type.Error, 5f);
                return;
            }
            Player target = player.GetClosestPlayer();
            if (target == null)
            {
                player.Notify(NotifTitle, "Aucun joueur à proximité.", NotificationManager.Type.Warning, 5f);
                return;
            }
            ControlMenu(player, target);
        }

        private async void ControlMenu(Player officer, Player target)
        {
            List<AutoEcoleLicense> records = await AutoEcoleLicense.GetAll(target.character.Id);

            Panel panel = PanelHelper.Create($"Contrôle - {target.FullName}", UIPanel.PanelType.Tab, officer, () => ControlMenu(officer, target));

            foreach (LicenseConfig license in Config.Permis)
            {
                AutoEcoleLicense record = records.FirstOrDefault(r => r.Category == license.Code);
                panel.AddTabLine($"{license.Nom} : {LicenseStatus(license, record)}", _ =>
                {
                    if (record == null || !record.IsValid)
                    {
                        officer.Notify(NotifTitle, $"{target.FullName} n'a pas de {license.Nom} valide.", NotificationManager.Type.Warning, 5f);
                        panel.Refresh();
                        return;
                    }
                    RemovePointsPanel(officer, target, license);
                });
            }

            panel.NextButton("Retirer des points", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        private async void RemovePointsPanel(Player officer, Player target, LicenseConfig license)
        {
            AutoEcoleLicense record = await AutoEcoleLicense.Get(target.character.Id, license.Code);

            Panel panel = PanelHelper.Create($"Retrait de points - {license.Nom}", UIPanel.PanelType.Input, officer, () => RemovePointsPanel(officer, target, license));
            panel.TextLines.Add($"{target.FullName} : {record.Points}/{license.PointsMax} points.");
            panel.TextLines.Add("Combien de points retirer ?");
            panel.SetInputPlaceholder($"1 - {record.Points}");

            panel.PreviousButtonWithAction("Retirer", async () =>
            {
                if (!int.TryParse(panel.inputText, out int amount) || amount <= 0)
                {
                    officer.Notify(NotifTitle, "Nombre de points invalide.", NotificationManager.Type.Error, 5f);
                    return false;
                }
                await ChangePoints(officer, target, license, -amount);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Ajoute (ou retire si négatif) des points ; à 0 point le permis est annulé.</summary>
        private async Task ChangePoints(Player actor, Player target, LicenseConfig license, int delta)
        {
            AutoEcoleLicense record = await AutoEcoleLicense.Get(target.character.Id, license.Code);
            if (!record.IsValid)
            {
                actor.Notify(NotifTitle, $"{target.FullName} n'a pas de {license.Nom} valide.", NotificationManager.Type.Warning, 5f);
                return;
            }

            record.Points = Math.Max(0, Math.Min(license.PointsMax, record.Points + delta));
            if (record.Points == 0)
            {
                record.Obtained = false;
                record.Registered = false;
            }
            await record.Save();
            SyncNative(target, license, record);

            if (record.Points == 0)
            {
                actor.Notify(NotifTitle, $"Le {license.Nom} de {target.FullName} est annulé (0 point).", NotificationManager.Type.Warning, 6f);
                target.Notify(NotifTitle, $"Votre {license.Nom} est annulé : vous n'avez plus de points. Repassez-le à l'auto-école.", NotificationManager.Type.Error, 8f);
            }
            else
            {
                string change = delta < 0 ? $"{-delta} point(s) retiré(s)" : $"{delta} point(s) ajouté(s)";
                actor.Notify(NotifTitle, $"{change} : {target.FullName} a {record.Points}/{license.PointsMax} points.", NotificationManager.Type.Success, 5f);
                if (actor != target)
                    target.Notify(NotifTitle, $"{change} sur votre {license.Nom} : {record.Points}/{license.PointsMax} points.", delta < 0 ? NotificationManager.Type.Warning : NotificationManager.Type.Info, 6f);
            }
        }

        // ------------------------------------------------------------------
        //  Administration
        // ------------------------------------------------------------------

        private static bool IsStaff(Player player)
        {
            if (player.IsAdmin && player.serviceAdmin) return true;
            player.Notify(NotifTitle, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
            return false;
        }

        public void AdminMenu(Player player)
        {
            if (!IsStaff(player)) return;

            Panel panel = PanelHelper.Create($"{Config.NomAutoEcole} - Administration", UIPanel.PanelType.Tab, player, () => AdminMenu(player));
            panel.AddTabLine("Placer / gérer les auto-écoles", _ => new AutoEcolePattern(false) { Context = this }.CreateOrGenerate(player));
            panel.AddTabLine("Gérer le joueur le plus proche", _ =>
            {
                Player target = player.GetClosestPlayer();
                if (target == null)
                {
                    player.Notify(NotifTitle, "Aucun joueur à proximité.", NotificationManager.Type.Warning, 5f);
                    panel.Refresh();
                    return;
                }
                AdminPlayer(player, target);
            });
            panel.AddTabLine("Me gérer moi-même", _ => AdminPlayer(player, player));
            panel.AddTabLine("Recharger la configuration", _ =>
            {
                if (LoadConfig())
                    player.Notify(NotifTitle, $"Configuration rechargée ({Config.Permis.Count} permis, {Config.Permis.Sum(l => l.Questions.Count)} questions).", NotificationManager.Type.Success, 5f);
                else
                    player.Notify(NotifTitle, "config.json invalide, voir la console du serveur.", NotificationManager.Type.Error, 6f);
                panel.Refresh();
            });

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        private async void AdminPlayer(Player admin, Player target)
        {
            List<AutoEcoleLicense> records = await AutoEcoleLicense.GetAll(target.character.Id);

            Panel panel = PanelHelper.Create($"Admin - {target.FullName}", UIPanel.PanelType.Tab, admin, () => AdminPlayer(admin, target));
            foreach (LicenseConfig license in Config.Permis)
            {
                AutoEcoleLicense record = records.FirstOrDefault(r => r.Category == license.Code);
                panel.AddTabLine($"{license.Nom} : {LicenseStatus(license, record)}", _ => AdminLicense(admin, target, license));
            }

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AdminLicense(Player admin, Player target, LicenseConfig license)
        {
            Panel panel = PanelHelper.Create($"Admin - {license.Nom} - {target.FullName}", UIPanel.PanelType.Tab, admin, () => AdminLicense(admin, target, license));

            panel.AddTabLine(Color($"Donner le permis ({license.PointsMax} points)", Colors.Success), async _ =>
            {
                AutoEcoleLicense record = await AutoEcoleLicense.Get(target.character.Id, license.Code);
                record.Obtained = true;
                record.Registered = false;
                record.Points = license.PointsMax;
                record.ObtainedAt = DateUtils.GetCurrentTime();
                record.LastFailAt = 0;
                await record.Save();
                SyncNative(target, license, record);
                admin.Notify(NotifTitle, $"{license.Nom} donné à {target.FullName}.", NotificationManager.Type.Success, 5f);
                if (admin != target)
                    target.Notify(NotifTitle, $"Le staff vous a délivré le {license.Nom}.", NotificationManager.Type.Info, 6f);
                panel.Refresh();
            });
            panel.AddTabLine(Color("Retirer le permis", Colors.Error), async _ =>
            {
                AutoEcoleLicense record = await AutoEcoleLicense.Get(target.character.Id, license.Code);
                if (record.Id != 0)
                {
                    await record.Delete();
                    SyncNative(target, license, AutoEcoleLicense.New(target.character.Id, license.Code));
                }
                admin.Notify(NotifTitle, $"{license.Nom} de {target.FullName} supprimé (inscription et historique compris).", NotificationManager.Type.Success, 5f);
                panel.Refresh();
            });
            panel.AddTabLine("Ajouter / retirer des points", _ => AdminPointsPanel(admin, target, license));
            panel.AddTabLine("Annuler le délai d'attente", async _ =>
            {
                AutoEcoleLicense record = await AutoEcoleLicense.Get(target.character.Id, license.Code);
                record.LastFailAt = 0;
                record.LastCourseAt = 0;
                await record.Save();
                admin.Notify(NotifTitle, $"Délais réinitialisés pour {target.FullName}.", NotificationManager.Type.Success, 5f);
                panel.Refresh();
            });
            panel.AddTabLine("Inscrire gratuitement", async _ =>
            {
                AutoEcoleLicense record = await AutoEcoleLicense.Get(target.character.Id, license.Code);
                record.Registered = true;
                await record.Save();
                admin.Notify(NotifTitle, $"{target.FullName} est inscrit au {license.Nom}.", NotificationManager.Type.Success, 5f);
                panel.Refresh();
            });

            panel.SelectTabButton();
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void AdminPointsPanel(Player admin, Player target, LicenseConfig license)
        {
            Panel panel = PanelHelper.Create($"Points - {license.Nom}", UIPanel.PanelType.Input, admin, () => AdminPointsPanel(admin, target, license));
            panel.TextLines.Add($"Points à ajouter (ex. 3) ou à retirer (ex. -3) pour {target.FullName}.");
            panel.SetInputPlaceholder("-3");
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                if (!int.TryParse(panel.inputText, out int delta) || delta == 0)
                {
                    admin.Notify(NotifTitle, "Nombre invalide.", NotificationManager.Type.Error, 5f);
                    return false;
                }
                await ChangePoints(admin, target, license, delta);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Synchronisation avec le permis B natif du jeu
        // ------------------------------------------------------------------

        /// <summary>
        /// Recopie le permis lié au jeu (Permis B par défaut) dans la fiche du personnage
        /// (PermisB / PermisPoints / HasCode) pour que la carte d'identité et les autres
        /// plugins voient le même permis.
        /// </summary>
        private static void SyncNative(Player player, LicenseConfig license, AutoEcoleLicense record)
        {
            if (!license.LieAuPermisDuJeu || player?.character == null) return;

            player.character.PermisB = record.IsValid;
            player.character.PermisPoints = record.IsValid ? record.Points : 0;
            if (record.IsValid)
                player.character.HasCode = true;
            _ = player.Save();
        }

        private async void SyncOnSpawn(Player player)
        {
            LicenseConfig license = Config.Permis.FirstOrDefault(l => l.LieAuPermisDuJeu);
            if (license == null) return;

            AutoEcoleLicense record = await AutoEcoleLicense.Get(player.character.Id, license.Code);
            if (record.Id == 0 && player.character.PermisB)
            {
                // Permis obtenu avant l'installation du plugin (auto-école du jeu) : on le reprend
                record.Obtained = true;
                record.Points = player.character.PermisPoints > 0
                    ? Math.Min(player.character.PermisPoints, license.PointsMax)
                    : license.PointsMax;
                record.ObtainedAt = DateUtils.GetCurrentTime();
                await record.Save();
            }
            SyncNative(player, license, record);
        }

        // ------------------------------------------------------------------
        //  Utilitaires
        // ------------------------------------------------------------------

        private static string Letter(int index) => ((char)('A' + index)).ToString();

        private static string Price(int price) =>
            price <= 0 ? "Gratuit" : string.Format(CultureInfo.InvariantCulture, "{0:#,0} €", price).Replace(',', ' ');
    }
}

namespace AutoEcole
{
    // ----------------------------------------------------------------------
    //  Configuration (Plugins/AutoEcole/config.json)
    // ----------------------------------------------------------------------

    public class AutoEcoleConfig
    {
        public string NomAutoEcole { get; set; } = "Auto-École By Loris Strange";

        /// <summary>Questions tirées au hasard par examen (10 maximum).</summary>
        public int QuestionsParExamen { get; set; } = 10;

        /// <summary>Bonnes réponses nécessaires pour réussir.</summary>
        public int BonnesReponsesRequises { get; set; } = 8;

        /// <summary>Attente après un échec avant de pouvoir repasser l'examen.</summary>
        public int DelaiApresEchecMinutes { get; set; } = 5;

        /// <summary>false : l'inscription couvre toutes les tentatives jusqu'à la réussite.</summary>
        public bool PayerAChaqueTentative { get; set; } = false;

        /// <summary>Après l'examen, permet de revoir les questions ratées et la bonne réponse.</summary>
        public bool AfficherCorrection { get; set; } = true;

        public StageConfig StageRecuperation { get; set; } = new StageConfig();

        public List<LicenseConfig> Permis { get; set; } = new List<LicenseConfig>();

        public void Validate(string source)
        {
            if (Permis == null) Permis = new List<LicenseConfig>();
            if (StageRecuperation == null) StageRecuperation = new StageConfig();
            if (string.IsNullOrWhiteSpace(NomAutoEcole)) NomAutoEcole = "Auto-École";

            if (QuestionsParExamen > AutoEcolePlugin.MaxQuestions)
                Logger.LogWarning(source, $"QuestionsParExamen limité à {AutoEcolePlugin.MaxQuestions}.");

            Permis = Permis.Where(l => l != null && !string.IsNullOrWhiteSpace(l.Code))
                           .GroupBy(l => l.Code).Select(g => g.First()).ToList();

            foreach (LicenseConfig license in Permis)
            {
                if (string.IsNullOrWhiteSpace(license.Nom)) license.Nom = $"Permis {license.Code}";
                if (license.PointsMax <= 0) license.PointsMax = 12;
                if (license.Questions == null) license.Questions = new List<QuestionConfig>();

                int before = license.Questions.Count;
                license.Questions = license.Questions.Where(q => q != null && q.IsValid()).ToList();
                if (license.Questions.Count != before)
                    Logger.LogWarning(source, $"{license.Nom} : {before - license.Questions.Count} question(s) ignorée(s) (2 à 4 réponses, BonneReponse entre 1 et le nombre de réponses).");

                int count = Math.Min(Math.Min(QuestionsParExamen, AutoEcolePlugin.MaxQuestions), license.Questions.Count);
                if (license.Questions.Count == 0)
                    Logger.LogWarning(source, $"{license.Nom} : aucune question, l'examen est impossible.");
                else if (count < BonnesReponsesRequises)
                    Logger.LogWarning(source, $"{license.Nom} : seulement {license.Questions.Count} question(s), toutes les réponses devront être bonnes.");
            }
        }

        public static AutoEcoleConfig CreateDefault()
        {
            return new AutoEcoleConfig
            {
                Permis = new List<LicenseConfig>
                {
                    new LicenseConfig
                    {
                        Code = "B",
                        Nom = "Permis B",
                        Description = "Voitures et véhicules légers (moins de 3,5 tonnes).",
                        Prix = 1500,
                        PointsMax = 12,
                        LieAuPermisDuJeu = true,
                        Questions = DefaultQuestions.PermisB()
                    },
                    new LicenseConfig
                    {
                        Code = "C",
                        Nom = "Permis C (poids lourd)",
                        Description = "Camions de transport de marchandises de plus de 3,5 tonnes.",
                        Prix = 5000,
                        PointsMax = 12,
                        PermisRequis = "B",
                        Questions = DefaultQuestions.PermisC()
                    }
                }
            };
        }
    }

    public class StageConfig
    {
        public bool Actif { get; set; } = true;
        public int Prix { get; set; } = 750;
        public int PointsRecuperes { get; set; } = 4;
        public int DelaiEntreStagesHeures { get; set; } = 24;
    }

    public class LicenseConfig
    {
        /// <summary>Identifiant unique du permis (ne pas modifier une fois des joueurs inscrits).</summary>
        public string Code { get; set; }
        public string Nom { get; set; }
        public string Description { get; set; }
        public int Prix { get; set; }
        public int PointsMax { get; set; } = 12;

        /// <summary>Code d'un permis à posséder avant de pouvoir s'inscrire (ex. "B" pour le C).</summary>
        public string PermisRequis { get; set; }

        /// <summary>Recopie ce permis dans le permis B natif du jeu (fiche personnage).</summary>
        public bool LieAuPermisDuJeu { get; set; }

        public List<QuestionConfig> Questions { get; set; } = new List<QuestionConfig>();
    }

    public class QuestionConfig
    {
        public string Question { get; set; }
        public List<string> Reponses { get; set; } = new List<string>();

        /// <summary>Numéro de la bonne réponse : 1 = première réponse de la liste.</summary>
        public int BonneReponse { get; set; } = 1;

        public QuestionConfig() { }

        public QuestionConfig(string question, int bonneReponse, params string[] reponses)
        {
            Question = question;
            BonneReponse = bonneReponse;
            Reponses = reponses.ToList();
        }

        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(Question)
                   && Reponses != null && Reponses.Count >= 2 && Reponses.Count <= 4
                   && Reponses.All(r => !string.IsNullOrWhiteSpace(r))
                   && BonneReponse >= 1 && BonneReponse <= Reponses.Count;
        }
    }

    public static class DefaultQuestions
    {
        public static List<QuestionConfig> PermisB()
        {
            return new List<QuestionConfig>
            {
                new QuestionConfig("En agglomération, sauf indication contraire, la vitesse est limitée à :", 2,
                    "30 km/h", "50 km/h", "70 km/h"),
                new QuestionConfig("Sur autoroute, par temps sec, la vitesse maximale autorisée est de :", 2,
                    "110 km/h", "130 km/h", "150 km/h"),
                new QuestionConfig("Sur autoroute, par temps de pluie, la vitesse maximale autorisée est de :", 1,
                    "110 km/h", "120 km/h", "130 km/h"),
                new QuestionConfig("Le taux d'alcool maximal autorisé pour un conducteur confirmé est de :", 2,
                    "0,2 g/l de sang", "0,5 g/l de sang", "0,8 g/l de sang"),
                new QuestionConfig("Le feu passe à l'orange fixe :", 2,
                    "J'accélère pour passer", "Je m'arrête, sauf si l'arrêt est dangereux", "Je klaxonne et je passe"),
                new QuestionConfig("Au panneau STOP, je dois :", 3,
                    "Ralentir et passer si personne n'arrive", "M'arrêter seulement si un véhicule arrive", "Marquer un arrêt complet et céder le passage"),
                new QuestionConfig("Tenir son téléphone en main en conduisant est :", 3,
                    "Autorisé à faible vitesse", "Autorisé en agglomération", "Interdit"),
                new QuestionConfig("À une intersection sans signalisation, la priorité est :", 1,
                    "À droite", "À gauche", "Au véhicule le plus gros"),
                new QuestionConfig("La ceinture de sécurité est obligatoire :", 2,
                    "Uniquement pour les places avant", "Pour tous les occupants du véhicule", "Uniquement hors agglomération"),
                new QuestionConfig("Sur autoroute, la distance de sécurité minimale correspond à :", 2,
                    "1 seconde", "2 secondes", "5 secondes"),
                new QuestionConfig("Un piéton est engagé sur un passage piéton :", 2,
                    "Je klaxonne pour qu'il se dépêche", "Je m'arrête pour le laisser traverser", "Je passe avant lui s'il est loin"),
                new QuestionConfig("Un panneau triangulaire à bordure rouge indique :", 1,
                    "Un danger", "Une obligation", "Une interdiction"),
                new QuestionConfig("Un panneau rond à fond bleu indique :", 2,
                    "Une interdiction", "Une obligation", "Une fin d'interdiction"),
                new QuestionConfig("En cas de panne sur le bord de la route, avant de sortir du véhicule :", 1,
                    "J'enfile mon gilet jaune", "Je pose d'abord le triangle", "Je reste dans le véhicule sans rien signaler"),
                new QuestionConfig("Les feux de croisement sont obligatoires :", 1,
                    "La nuit et quand la visibilité est mauvaise", "Uniquement sur autoroute", "Jamais en agglomération"),
                new QuestionConfig("Dépasser par la droite est :", 2,
                    "Toujours autorisé", "Interdit, sauf cas particuliers (véhicule qui tourne à gauche, files...)", "Autorisé si la voie de gauche est occupée"),
                new QuestionConfig("Un véhicule d'urgence arrive derrière moi, sirène et gyrophare allumés :", 3,
                    "J'accélère pour rester devant", "Je m'arrête immédiatement au milieu de la voie", "Je facilite son passage en me décalant"),
            };
        }

        public static List<QuestionConfig> PermisC()
        {
            return new List<QuestionConfig>
            {
                new QuestionConfig("Le permis C permet de conduire :", 1,
                    "Un camion de plus de 3,5 tonnes", "Un autobus de passagers", "Une moto de forte cylindrée"),
                new QuestionConfig("Sur autoroute, un poids lourd de plus de 12 tonnes est limité à :", 2,
                    "80 km/h", "90 km/h", "110 km/h"),
                new QuestionConfig("Le chronotachygraphe sert à :", 1,
                    "Enregistrer les temps de conduite, de repos et la vitesse", "Mesurer la consommation de carburant", "Guider le conducteur par GPS"),
                new QuestionConfig("La durée maximale de conduite continue avant une pause est de :", 2,
                    "2 heures", "4 heures 30", "6 heures"),
                new QuestionConfig("Après 4 h 30 de conduite, la pause obligatoire dure au minimum :", 2,
                    "15 minutes", "45 minutes", "2 heures"),
                new QuestionConfig("En règle générale, la durée de conduite journalière maximale est de :", 1,
                    "9 heures", "12 heures", "15 heures"),
                new QuestionConfig("Les angles morts d'un poids lourd sont :", 1,
                    "Beaucoup plus grands que ceux d'une voiture, surtout à droite", "Identiques à ceux d'une voiture", "Inexistants grâce aux rétroviseurs"),
                new QuestionConfig("Dans une longue descente, pour éviter l'échauffement des freins :", 2,
                    "Je roule au point mort", "J'utilise le ralentisseur et un rapport adapté", "Je freine en continu"),
                new QuestionConfig("Hors agglomération, la distance minimale entre deux poids lourds qui se suivent est de :", 2,
                    "30 mètres", "50 mètres", "100 mètres"),
                new QuestionConfig("L'arrimage du chargement :", 1,
                    "Doit être vérifié avant le départ, le conducteur en est responsable", "Est facultatif sur de courtes distances", "Ne concerne que le chargeur"),
                new QuestionConfig("Connaître la hauteur de son véhicule est important pour :", 3,
                    "Calculer sa consommation", "Choisir sa voie sur autoroute", "Passer sous les ponts et dans les tunnels"),
                new QuestionConfig("Par fort vent latéral avec une remorque vide :", 1,
                    "Le risque de renversement augmente, je réduis ma vitesse", "Il n'y a aucun risque particulier", "J'accélère pour stabiliser le véhicule"),
            };
        }
    }

    // ----------------------------------------------------------------------
    //  Examen en cours (mémoire uniquement)
    // ----------------------------------------------------------------------

    public class ExamQuestion
    {
        public string Text;
        public List<string> Answers;
        public int Correct; // index 0-based

        /// <summary>Copie de la question avec les réponses mélangées.</summary>
        public static ExamQuestion Shuffled(QuestionConfig source, Random random)
        {
            List<int> order = Enumerable.Range(0, source.Reponses.Count).OrderBy(_ => random.Next()).ToList();
            return new ExamQuestion
            {
                Text = source.Question,
                Answers = order.Select(i => source.Reponses[i]).ToList(),
                Correct = order.IndexOf(source.BonneReponse - 1)
            };
        }
    }

    public class ExamSession
    {
        public Player Player { get; }
        public int CharacterId { get; }
        public LicenseConfig License { get; }
        public List<ExamQuestion> Questions { get; }
        public int Required { get; }
        public List<int> Choices { get; } = new List<int>();
        public int Index { get; set; }

        public ExamSession(Player player, LicenseConfig license, List<ExamQuestion> questions, int required)
        {
            Player = player;
            CharacterId = player.character.Id;
            License = license;
            Questions = questions;
            Required = required;
        }

        public int CorrectCount => Choices.Where((choice, i) => Questions[i].Correct == choice).Count();

        /// <summary>Questions ratées : (numéro affiché, question, réponse choisie ou -1).</summary>
        public IEnumerable<(int number, ExamQuestion question, int choice)> Mistakes =>
            Questions.Select((q, i) => (i + 1, q, i < Choices.Count ? Choices[i] : -1))
                     .Where(m => m.Item3 != m.Item2.Correct);
    }

    // ----------------------------------------------------------------------
    //  Base de données : permis des joueurs
    // ----------------------------------------------------------------------

    public class AutoEcoleLicense : ModEntity<AutoEcoleLicense>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int CharacterId { get; set; }

        /// <summary>Code du permis dans la config ("B", "C"...).</summary>
        public string Category { get; set; }

        /// <summary>Inscription payée, examen pas encore réussi.</summary>
        public bool Registered { get; set; }

        public bool Obtained { get; set; }

        public int Points { get; set; }

        public long ObtainedAt { get; set; }

        public long LastFailAt { get; set; }

        public int Attempts { get; set; }

        /// <summary>Dernier stage de récupération de points.</summary>
        public long LastCourseAt { get; set; }

        [Ignore]
        public bool IsValid => Obtained && Points > 0;

        public static AutoEcoleLicense New(int characterId, string category)
        {
            return new AutoEcoleLicense { CharacterId = characterId, Category = category };
        }

        public static async Task<AutoEcoleLicense> Get(int characterId, string category)
        {
            List<AutoEcoleLicense> found = await Query(l => l.CharacterId == characterId && l.Category == category);
            return found.FirstOrDefault() ?? New(characterId, category);
        }

        public static Task<List<AutoEcoleLicense>> GetAll(int characterId)
        {
            return Query(l => l.CharacterId == characterId);
        }
    }
}

namespace AutoEcole
{
    /// <summary>
    /// Modèle d'auto-école placé par le staff (AAMenu > Points bleus ou /autoecole).
    /// Chaque placement crée un point bleu qui ouvre le menu de l'auto-école.
    /// </summary>
    public class AutoEcolePattern : ModEntity<AutoEcolePattern>, PatternData
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PatternName { get; set; }

        [Ignore]
        public string TypeName { get; set; } = nameof(AutoEcolePattern);

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        public AutoEcolePattern() { }

        public AutoEcolePattern(bool isCreated)
        {
            TypeName = nameof(AutoEcolePattern);
        }

        public void OnPlayerTrigger(Player player)
        {
            AutoEcolePlugin.Instance.OpenSchool(player, PatternName);
        }

        public async Task SetProperties(int id)
        {
            AutoEcolePattern result = await Query(id);
            Id = id;
            TypeName = nameof(AutoEcolePattern);
            PatternName = result?.PatternName;
        }

        /// <summary>Menu staff : choisir un modèle et placer une auto-école à sa position.</summary>
        public async void CreateOrGenerate(Player player)
        {
            if (!player.IsAdmin) return;
            if (Context == null) Context = AutoEcolePlugin.Instance;

            List<AutoEcolePattern> patterns = await QueryAll();

            Panel panel = Context.PanelHelper.Create("Auto-École - Placer un point", UIPanel.PanelType.Tab, player, () => CreateOrGenerate(player));

            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle, créez-en un", _ => { });

            foreach (AutoEcolePattern pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}", async _ =>
                {
                    pattern.TypeName = nameof(AutoEcolePattern);
                    pattern.Context = Context;
                    if (await Context.PointHelper.CreateNPoint(player, pattern))
                        player.Notify(AutoEcolePlugin.NotifTitle, $"Auto-école « {pattern.PatternName} » placée à votre position.", NotificationManager.Type.Success, 5f);
                    else
                        player.Notify(AutoEcolePlugin.NotifTitle, "Erreur lors de la création du point.", NotificationManager.Type.Error, 5f);
                });
            }

            if (patterns.Count > 0)
                panel.NextButton("Placer ici", () => panel.SelectTab());
            panel.NextButton("Nouveau modèle", () => SetPatternData(player));
            panel.NextButton("Modèles", async () => await GetPatternData(player, true));
            panel.NextButton("Points", async () => await GetNPoints(player));
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Création d'un nouveau modèle d'auto-école.</summary>
        public void SetPatternData(Player player)
        {
            if (Context == null) Context = AutoEcolePlugin.Instance;

            Panel panel = Context.PanelHelper.Create("Auto-École - Nouveau modèle", UIPanel.PanelType.Input, player, () => SetPatternData(player));
            panel.TextLines.Add("Nom de l'auto-école (affiché aux joueurs)");
            panel.SetInputPlaceholder(AutoEcolePlugin.Instance.Config.NomAutoEcole);

            panel.PreviousButtonWithAction("Créer", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name))
                    name = AutoEcolePlugin.Instance.Config.NomAutoEcole;

                AutoEcolePattern pattern = new AutoEcolePattern(false) { PatternName = name };
                if (!await pattern.Save())
                {
                    player.Notify(AutoEcolePlugin.NotifTitle, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
                    return false;
                }

                player.Notify(AutoEcolePlugin.NotifTitle, $"Modèle « {name} » créé. Choisissez-le puis « Placer ici ».", NotificationManager.Type.Success, 5f);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Liste des modèles : renommer ou supprimer (avec tous ses points).</summary>
        public async Task GetPatternData(Player player, bool forEdit)
        {
            if (Context == null) Context = AutoEcolePlugin.Instance;

            List<AutoEcolePattern> patterns = await QueryAll();
            string action = "";

            Panel panel = Context.PanelHelper.Create("Auto-École - Modèles", UIPanel.PanelType.Tab, player, async () => await GetPatternData(player, forEdit));

            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle", _ => { });

            foreach (AutoEcolePattern pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}", async _ =>
                {
                    pattern.TypeName = nameof(AutoEcolePattern);
                    pattern.Context = Context;

                    if (action == "rename")
                    {
                        RenamePattern(player, pattern);
                    }
                    else if (action == "delete")
                    {
                        await Context.PointHelper.DeleteNPointsByPattern(player, pattern);
                        if (await pattern.Delete())
                            player.Notify(AutoEcolePlugin.NotifTitle, $"Modèle « {pattern.PatternName} » et ses points supprimés.", NotificationManager.Type.Success, 5f);
                        else
                            player.Notify(AutoEcolePlugin.NotifTitle, "Erreur lors de la suppression.", NotificationManager.Type.Error, 5f);
                        panel.Refresh();
                    }
                });
            }

            if (patterns.Count > 0 && forEdit)
            {
                panel.NextButton("Renommer", () => { action = "rename"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void RenamePattern(Player player, AutoEcolePattern pattern)
        {
            Panel panel = Context.PanelHelper.Create("Auto-École - Renommer", UIPanel.PanelType.Input, player, () => RenamePattern(player, pattern));
            panel.TextLines.Add($"Nouveau nom pour « {pattern.PatternName} »");
            panel.SetInputPlaceholder(pattern.PatternName ?? "Auto-École");

            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name)) return false;
                pattern.PatternName = name;
                return await pattern.Save();
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Liste de tous les points placés : se téléporter, déplacer ou supprimer.</summary>
        public async Task GetNPoints(Player player)
        {
            if (Context == null) Context = AutoEcolePlugin.Instance;

            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == nameof(AutoEcolePattern));
            Dictionary<int, string> names = (await QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);
            string action = "";

            Panel panel = Context.PanelHelper.Create("Auto-École - Points placés", UIPanel.PanelType.Tab, player, async () => await GetNPoints(player));

            if (points.Count == 0)
                panel.AddTabLine("Aucune auto-école placée", _ => { });

            foreach (NPoint point in points)
            {
                string name = names.TryGetValue(point.PatternId, out string n) ? n : "?";
                panel.AddTabLine($"Point #{point.Id} - {name}", async _ =>
                {
                    switch (action)
                    {
                        case "tp":
                            Context.PointHelper.PlayerSetPositionToNPoint(player, point);
                            break;
                        case "move":
                            if (await Context.PointHelper.SetNPointPosition(player, point))
                                player.Notify(AutoEcolePlugin.NotifTitle, "Point déplacé à votre position.", NotificationManager.Type.Success, 5f);
                            break;
                        case "delete":
                            await Context.PointHelper.DeleteNPoint(point);
                            player.Notify(AutoEcolePlugin.NotifTitle, $"Point #{point.Id} supprimé.", NotificationManager.Type.Success, 5f);
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
