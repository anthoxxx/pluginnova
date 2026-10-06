using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Life;
using Life.DB;
using Life.Network;
using Life.UI;
using ModKit.Helper;
using ModKit.Helper.JobHelper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.ORM;
using ModKit.Utils;
using Newtonsoft.Json;
using SQLite;
using static ModKit.Helper.TextFormattingHelper;

namespace FranceTravail
{
    /// <summary>
    /// France Travail : les joueurs s'actualisent (questionnaire) depuis AAMenu, et les
    /// entreprises « France Travail » reçoivent les actualisations en temps réel dans leur
    /// espace patron, où elles peuvent les traiter et proposer des emplois aux demandeurs.
    /// </summary>
    public class FranceTravailPlugin : ModKit.ModKit
    {
        /// <summary>Nom de l'activité personnalisée AAMenu à attribuer aux entreprises France Travail.</summary>
        public const string ActivityName = "France Travail";

        public FranceTravailConfig Config { get; private set; }
        public PlayerMenu PlayerMenu { get; private set; }
        public AgencyMenu AgencyMenu { get; private set; }

        public FranceTravailPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "anthoxxx");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            Config = FranceTravailConfig.Load(Path.Combine(pluginsPath, "FranceTravail"));

            Orm.RegisterTable<Actualisation>();
            Orm.RegisterTable<JobProposal>();

            PlayerMenu = new PlayerMenu(this);
            AgencyMenu = new AgencyMenu(this);

            InitActivity();

            if (AAMenu.AAMenu.menu != null)
            {
                // Côté joueur : AAMenu > Interactions > France Travail
                AAMenu.Menu.AddInteractionTabLine(PluginInformations, "France Travail",
                    ui => PlayerMenu.Open(PanelHelper.ReturnPlayerFromPanel(ui)));

                // Côté entreprise : AAMenu > Métier, visible seulement pour les sociétés
                // ayant l'activité personnalisée « France Travail ».
                AAMenu.Menu.AddBizTabLine(PluginInformations, null, new CustomActivity { Name = ActivityName },
                    "France Travail - Espace patron", ui => AgencyMenu.Open(PanelHelper.ReturnPlayerFromPanel(ui)));
            }
            else
            {
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : utilisez /francetravail et /ftpatron.");
            }

            RegisterCommands();

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", "initialisé");
        }

        /// <summary>Crée l'activité personnalisée « France Travail » si elle n'existe pas encore.</summary>
        private async void InitActivity()
        {
            try
            {
                await JobHelper.AddCustomActivity(ActivityName);
            }
            catch (Exception e)
            {
                Logger.LogError(PluginInformations.SourceName, $"Création de l'activité {ActivityName} : {e.Message}");
            }
        }

        private void RegisterCommands()
        {
            new SChatCommand("/francetravail", new string[] { "/ft" }, "Ouvrir mon espace France Travail", "/francetravail",
                (Action<Player, string[]>)((player, args) => PlayerMenu.Open(player))).Register();

            new SChatCommand("/ftpatron", "Espace patron France Travail", "/ftpatron",
                (Action<Player, string[]>)((player, args) => AgencyMenu.Open(player))).Register();

            // Alternative à AAMenu > Administration > Activités personnalisées
            new SChatCommand("/ftagence", "Déclarer une entreprise comme agence France Travail (staff)", "/ftagence [id entreprise]",
                (Action<Player, string[]>)((player, args) => LinkAgency(player, args))).Register();
        }

        private async void LinkAgency(Player player, string[] args)
        {
            if (!player.IsAdmin || !player.serviceAdmin)
            {
                player.Notify(ActivityName, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
                return;
            }

            int bizId;
            if (args.Length > 0)
            {
                if (!int.TryParse(args[0], out bizId))
                {
                    player.Notify(ActivityName, "Usage : /ftagence [id entreprise]", NotificationManager.Type.Error, 5f);
                    return;
                }
            }
            else if (player.HasBiz)
            {
                bizId = player.biz.Id;
            }
            else
            {
                player.Notify(ActivityName, "Précisez l'id de l'entreprise ou faites partie de celle-ci.", NotificationManager.Type.Error, 5f);
                return;
            }

            Bizs biz = Nova.biz.FetchBiz(bizId);
            CustomActivity activity = await GetActivity();
            if (biz == null || activity == null)
            {
                player.Notify(ActivityName, "Entreprise ou activité introuvable.", NotificationManager.Type.Error, 5f);
                return;
            }

            if ((await GetAgencyBizIds()).Contains(bizId))
            {
                player.Notify(ActivityName, $"{biz.BizName} est déjà une agence France Travail.", NotificationManager.Type.Info, 5f);
                return;
            }

            if (await JobHelper.AddCustomBiz(bizId, activity.Id))
                player.Notify(ActivityName, $"{biz.BizName} est maintenant une agence France Travail.", NotificationManager.Type.Success, 5f);
            else
                player.Notify(ActivityName, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
        }

        // ------------------------------------------------------------------
        //  Agences (entreprises ayant l'activité « France Travail »)
        // ------------------------------------------------------------------

        public async Task<CustomActivity> GetActivity()
        {
            return (await CustomActivity.Query(a => a.Name == ActivityName)).FirstOrDefault();
        }

        public async Task<HashSet<int>> GetAgencyBizIds()
        {
            CustomActivity activity = await GetActivity();
            if (activity == null) return new HashSet<int>();
            int activityId = activity.Id;
            List<CustomBiz> bizs = await CustomBiz.Query(b => b.CustomActivityId == activityId);
            return new HashSet<int>(bizs.Select(b => b.BizId));
        }

        public async Task<bool> IsAgencyMember(Player player)
        {
            return player.HasBiz && (await GetAgencyBizIds()).Contains(player.biz.Id);
        }

        /// <summary>Le joueur a-t-il accès à l'espace patron ? (patron, gestionnaire, ou tous si configuré)</summary>
        public async Task<bool> CanManage(Player player)
        {
            if (!await IsAgencyMember(player)) return false;
            if (Config.AccesTousLesEmployes || PermissionUtils.PlayerIsOwner(player)) return true;
            return await PermissionUtils.PlayerCanManageTheEmployees(player);
        }

        /// <summary>Notifie tous les membres connectés des agences France Travail.</summary>
        public async Task NotifyAgencies(string message, NotificationManager.Type type)
        {
            HashSet<int> agencies = await GetAgencyBizIds();
            foreach (Player p in Nova.server.Players.ToList())
            {
                if (p != null && p.HasBiz && agencies.Contains(p.biz.Id))
                    p.Notify(ActivityName, message, type, 8f);
            }
        }

        public static Player FindOnlinePlayer(int characterId)
        {
            return Nova.server.Players.FirstOrDefault(p => p?.character != null && p.character.Id == characterId);
        }
    }

    // ======================================================================
    //  Configuration (Plugins/FranceTravail/config.json)
    // ======================================================================

    public class FranceTravailConfig
    {
        /// <summary>Délai minimum (en heures réelles) entre deux actualisations d'un même joueur.</summary>
        public int DelaiEntreActualisationsHeures { get; set; } = 24;

        /// <summary>false : seuls le patron et ceux qui gèrent les employés ont l'espace patron. true : tous les employés.</summary>
        public bool AccesTousLesEmployes { get; set; } = false;

        public List<string> Secteurs { get; set; } = new List<string>
        {
            "Commerce / Vente", "Restauration", "Transport / Livraison", "Sécurité", "Mécanique",
            "BTP", "Santé", "Administration", "Agriculture", "Services publics", "Peu importe"
        };

        public static FranceTravailConfig Load(string directory)
        {
            string path = Path.Combine(directory, "config.json");
            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(path))
                {
                    FranceTravailConfig config = JsonConvert.DeserializeObject<FranceTravailConfig>(File.ReadAllText(path));
                    if (config != null)
                    {
                        if (config.Secteurs == null || config.Secteurs.Count == 0) config.Secteurs = new FranceTravailConfig().Secteurs;
                        return config;
                    }
                }
                FranceTravailConfig defaults = new FranceTravailConfig();
                File.WriteAllText(path, JsonConvert.SerializeObject(defaults, Formatting.Indented));
                return defaults;
            }
            catch (Exception e)
            {
                Logger.LogError("FranceTravail", $"Lecture de {path} impossible ({e.Message}), configuration par défaut utilisée.");
                return new FranceTravailConfig();
            }
        }
    }

    // ======================================================================
    //  Données (base ModKit)
    // ======================================================================

    public enum ActualisationStatus { EnAttente = 0, Validee = 1, Refusee = 2 }
    public enum SearchStatus { Recherche = 0, EmploiTrouve = 1, NeRecherchePlus = 2 }
    public enum ProposalStatus { EnAttente = 0, Acceptee = 1, Refusee = 2 }

    /// <summary>Une actualisation = les réponses d'un joueur au questionnaire.</summary>
    public class Actualisation : ModEntity<Actualisation>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int CharacterId { get; set; }
        public string PlayerName { get; set; }
        public long CreatedAt { get; set; }

        public bool HasWorked { get; set; }
        public string HoursWorked { get; set; }
        public string Employer { get; set; }
        public bool SickLeave { get; set; }
        public bool Training { get; set; }
        public int Search { get; set; }
        public string Sector { get; set; }
        public bool Available { get; set; }
        public string Comment { get; set; }

        public int Status { get; set; }
        public string ProcessedBy { get; set; }
        public long ProcessedAt { get; set; }

        [Ignore]
        public bool IsSearching => Search == (int)SearchStatus.Recherche;

        public Actualisation() { }

        public string SearchLabel()
        {
            switch ((SearchStatus)Search)
            {
                case SearchStatus.Recherche: return "Recherche un emploi";
                case SearchStatus.EmploiTrouve: return "A trouvé un emploi";
                default: return "Ne recherche plus d'emploi";
            }
        }

        public string StatusLabel()
        {
            switch ((ActualisationStatus)Status)
            {
                case ActualisationStatus.Validee: return Color("Validée", Colors.Success);
                case ActualisationStatus.Refusee: return Color("Refusée", Colors.Error);
                default: return Color("En attente", Colors.Warning);
            }
        }

        /// <summary>Récapitulatif des réponses, une ligne par question.</summary>
        public List<string> Summary()
        {
            List<string> lines = new List<string>
            {
                $"{Bold("Demandeur :")} {PlayerName}",
                $"{Bold("Travaillé cette semaine :")} {(HasWorked ? $"Oui ({HoursWorked}{(string.IsNullOrEmpty(Employer) ? "" : $", chez {Employer}")})" : "Non")}",
                $"{Bold("Arrêt maladie :")} {YesNo(SickLeave)}",
                $"{Bold("Formation :")} {YesNo(Training)}",
                $"{Bold("Situation :")} {SearchLabel()}"
            };
            if (IsSearching)
            {
                lines.Add($"{Bold("Secteur recherché :")} {Sector}");
                lines.Add($"{Bold("Disponible immédiatement :")} {YesNo(Available)}");
            }
            if (!string.IsNullOrEmpty(Comment))
                lines.Add($"{Bold("Commentaire :")} {Comment}");
            return lines;
        }

        private static string YesNo(bool value) => value ? "Oui" : "Non";
    }

    /// <summary>Une proposition d'emploi envoyée par une agence à un demandeur.</summary>
    public class JobProposal : ModEntity<JobProposal>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int CharacterId { get; set; }
        public string PlayerName { get; set; }
        public string JobTitle { get; set; }
        public string Employer { get; set; }
        public string Details { get; set; }

        public int AgencyBizId { get; set; }
        public string AgencyName { get; set; }
        public string ProposedBy { get; set; }
        public long CreatedAt { get; set; }

        public int Status { get; set; }
        public long AnsweredAt { get; set; }

        public JobProposal() { }

        public string StatusLabel()
        {
            switch ((ProposalStatus)Status)
            {
                case ProposalStatus.Acceptee: return Color("Acceptée", Colors.Success);
                case ProposalStatus.Refusee: return Color("Refusée", Colors.Error);
                default: return Color("En attente", Colors.Warning);
            }
        }

        public List<string> Summary()
        {
            List<string> lines = new List<string>
            {
                $"{Bold("Poste :")} {JobTitle}",
                $"{Bold("Entreprise :")} {Employer}"
            };
            if (!string.IsNullOrEmpty(Details))
                lines.Add($"{Bold("Détails :")} {Details}");
            lines.Add($"{Bold("Proposé par :")} {ProposedBy} ({AgencyName}), le {FtTime.Format(CreatedAt)}");
            lines.Add($"{Bold("Statut :")} {StatusLabel()}");
            return lines;
        }
    }

    // ======================================================================
    //  Utilitaires
    // ======================================================================

    public static class FtTime
    {
        public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static string Format(long unix) =>
            DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("dd/MM HH:mm");
    }

    /// <summary>
    /// Vues « temps réel ». ModKit regroupe les panels par méthode qui les a créés et
    /// <see cref="Panel.OnRefreshAll"/> recharge ce panel chez tous les joueurs qui l'ont
    /// ouvert. On garde un panel par vue pour pouvoir déclencher ce rafraîchissement.
    /// Les méthodes qui créent ces panels doivent appeler PanelHelper.Create directement
    /// (pas dans une méthode async ni une lambda) et ne pas être inlinées.
    /// </summary>
    public static class Live
    {
        private static readonly Dictionary<string, Panel> views = new Dictionary<string, Panel>();

        public static void Track(string view, Panel panel) => views[view] = panel;

        public static void RefreshAll()
        {
            foreach (Panel panel in views.Values.ToList())
            {
                try { panel.OnRefreshAll(); }
                catch (Exception e) { Logger.LogError("FranceTravail", $"Rafraîchissement : {e.Message}"); }
            }
        }

        /// <summary>
        /// À appeler avant de quitter une vue suivie autrement que par les boutons ModKit,
        /// sinon un rafraîchissement la ferait réapparaître par-dessus le nouveau panel.
        /// </summary>
        public static void Leave(Panel panel) => panel.RemoveFromInstance?.Invoke(panel, EventArgs.Empty);
    }

    // ======================================================================
    //  Côté joueur : s'actualiser, historique, propositions d'emploi
    // ======================================================================

    public class PlayerMenu
    {
        private readonly FranceTravailPlugin ctx;

        public PlayerMenu(FranceTravailPlugin context)
        {
            ctx = context;
        }

        public async void Open(Player player)
        {
            if (player?.character == null) return;
            int characterId = player.character.Id;
            List<Actualisation> history = await Actualisation.Query(a => a.CharacterId == characterId);
            List<JobProposal> proposals = await JobProposal.Query(p => p.CharacterId == characterId);
            ShowHome(player, history.OrderByDescending(a => a.CreatedAt).ToList(), proposals);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowHome(Player player, List<Actualisation> history, List<JobProposal> proposals)
        {
            Panel panel = ctx.PanelHelper.Create("France Travail - Mon espace", UIPanel.PanelType.Tab, player, () => Open(player));
            Live.Track("Joueur." + nameof(ShowHome), panel);

            Actualisation last = history.FirstOrDefault();
            long nextAllowed = last == null ? 0 : last.CreatedAt + ctx.Config.DelaiEntreActualisationsHeures * 3600L;
            long remaining = nextAllowed - FtTime.Now();
            int pendingProposals = proposals.Count(p => p.Status == (int)ProposalStatus.EnAttente);

            if (remaining <= 0)
            {
                panel.AddTabLine(Color("M'actualiser", Colors.Success), _ =>
                {
                    Live.Leave(panel);
                    AskWorked(player, new Actualisation { CharacterId = player.character.Id, PlayerName = player.FullName });
                });
            }
            else
            {
                long hours = remaining / 3600, minutes = remaining % 3600 / 60;
                panel.AddTabLine(Color($"Prochaine actualisation dans {hours}h{minutes:00}", Colors.Grey), _ =>
                    player.Notify(FranceTravailPlugin.ActivityName, "Vous vous êtes déjà actualisé récemment.", NotificationManager.Type.Info, 5f));
            }

            panel.AddTabLine($"Mes actualisations ({history.Count})", _ => { Live.Leave(panel); ShowHistory(player, history); });
            panel.AddTabLine(pendingProposals > 0
                    ? Color($"Mes propositions d'emploi ({pendingProposals} nouvelle(s))", Colors.Warning)
                    : $"Mes propositions d'emploi ({proposals.Count})",
                _ => { Live.Leave(panel); OpenProposals(player); });

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---------------------------- Questionnaire ----------------------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskWorked(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation - Avez-vous travaillé cette semaine ?", UIPanel.PanelType.Tab, player, () => AskWorked(player, d));
            panel.AddTabLine("Oui, j'ai travaillé", _ => { d.HasWorked = true; AskHours(player, d); });
            panel.AddTabLine("Non, je n'ai pas travaillé", _ =>
            {
                d.HasWorked = false;
                d.HoursWorked = null;
                d.Employer = null;
                AskSickLeave(player, d);
            });
            ChoiceButtons(panel);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskHours(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation - Combien d'heures avez-vous travaillé ?", UIPanel.PanelType.Tab, player, () => AskHours(player, d));
            foreach (string hours in new[] { "Moins de 10 heures", "Entre 10 et 20 heures", "Entre 20 et 35 heures", "Plus de 35 heures" })
            {
                panel.AddTabLine(hours, _ => { d.HoursWorked = hours; AskEmployer(player, d); });
            }
            ChoiceButtons(panel);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskEmployer(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation - Employeur", UIPanel.PanelType.Input, player, () => AskEmployer(player, d));
            panel.TextLines.Add("Pour quelle entreprise avez-vous travaillé ? (facultatif)");
            panel.SetInputPlaceholder("Nom de l'entreprise");
            panel.AddButton("Valider", _ => { d.Employer = Clean(panel.inputText, 60); AskSickLeave(player, d); });
            panel.AddButton("Passer", _ => { d.Employer = null; AskSickLeave(player, d); });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskSickLeave(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation - Avez-vous été en arrêt maladie ?", UIPanel.PanelType.Tab, player, () => AskSickLeave(player, d));
            panel.AddTabLine("Oui, j'ai été en arrêt maladie", _ => { d.SickLeave = true; AskTraining(player, d); });
            panel.AddTabLine("Non", _ => { d.SickLeave = false; AskTraining(player, d); });
            ChoiceButtons(panel);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskTraining(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation - Avez-vous suivi une formation ?", UIPanel.PanelType.Tab, player, () => AskTraining(player, d));
            panel.AddTabLine("Oui, j'ai suivi une formation", _ => { d.Training = true; AskSearch(player, d); });
            panel.AddTabLine("Non", _ => { d.Training = false; AskSearch(player, d); });
            ChoiceButtons(panel);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskSearch(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation - Recherchez-vous un emploi ?", UIPanel.PanelType.Tab, player, () => AskSearch(player, d));
            panel.AddTabLine("Oui, je recherche un emploi", _ => { d.Search = (int)SearchStatus.Recherche; AskSector(player, d); });
            panel.AddTabLine("Non, j'ai trouvé un emploi", _ => { d.Search = (int)SearchStatus.EmploiTrouve; SkipSearchQuestions(player, d); });
            panel.AddTabLine("Non, je ne recherche plus d'emploi", _ => { d.Search = (int)SearchStatus.NeRecherchePlus; SkipSearchQuestions(player, d); });
            ChoiceButtons(panel);
        }

        private void SkipSearchQuestions(Player player, Actualisation d)
        {
            d.Sector = null;
            d.Available = false;
            AskComment(player, d);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskSector(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation - Quel secteur recherchez-vous ?", UIPanel.PanelType.Tab, player, () => AskSector(player, d));
            foreach (string sector in ctx.Config.Secteurs)
            {
                panel.AddTabLine(sector, _ => { d.Sector = sector; AskAvailable(player, d); });
            }
            ChoiceButtons(panel);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskAvailable(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation - Êtes-vous disponible immédiatement ?", UIPanel.PanelType.Tab, player, () => AskAvailable(player, d));
            panel.AddTabLine("Oui, je suis disponible", _ => { d.Available = true; AskComment(player, d); });
            panel.AddTabLine("Non, pas tout de suite", _ => { d.Available = false; AskComment(player, d); });
            ChoiceButtons(panel);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskComment(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation - Commentaire", UIPanel.PanelType.Input, player, () => AskComment(player, d));
            panel.TextLines.Add("Un message pour votre conseiller ? (facultatif)");
            panel.SetInputPlaceholder("Ex : je cherche un poste de nuit");
            panel.AddButton("Valider", _ => { d.Comment = Clean(panel.inputText, 200); ShowRecap(player, d); });
            panel.AddButton("Passer", _ => { d.Comment = null; ShowRecap(player, d); });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowRecap(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation - Récapitulatif", UIPanel.PanelType.Text, player, () => ShowRecap(player, d));
            panel.TextLines.AddRange(d.Summary());
            panel.TextLines.Add("");
            panel.TextLines.Add(Italic("Vérifiez vos réponses puis envoyez votre actualisation."));

            panel.CloseButtonWithAction(Color("Envoyer", Colors.Success), async () => await Submit(player, d));
            panel.PreviousButton("Modifier");
            panel.CloseButton("Annuler");
            panel.Display();
        }

        private async Task<bool> Submit(Player player, Actualisation d)
        {
            // Nouvelle vérification du délai (le menu a pu rester ouvert longtemps)
            int characterId = d.CharacterId;
            Actualisation last = (await Actualisation.Query(a => a.CharacterId == characterId))
                .OrderByDescending(a => a.CreatedAt).FirstOrDefault();
            if (last != null && FtTime.Now() - last.CreatedAt < ctx.Config.DelaiEntreActualisationsHeures * 3600L)
            {
                player.Notify(FranceTravailPlugin.ActivityName, "Vous vous êtes déjà actualisé récemment.", NotificationManager.Type.Error, 5f);
                return true;
            }

            d.CreatedAt = FtTime.Now();
            d.Status = (int)ActualisationStatus.EnAttente;
            if (!await d.Save())
            {
                player.Notify(FranceTravailPlugin.ActivityName, "Erreur lors de l'envoi, réessayez.", NotificationManager.Type.Error, 5f);
                return false;
            }

            player.Notify(FranceTravailPlugin.ActivityName, "Votre actualisation a bien été envoyée à France Travail.", NotificationManager.Type.Success, 6f);
            await ctx.NotifyAgencies($"Nouvelle actualisation de {d.PlayerName}.", NotificationManager.Type.Info);
            Live.RefreshAll();
            return true;
        }

        /// <summary>Boutons communs aux questions à choix.</summary>
        private static void ChoiceButtons(Panel panel)
        {
            panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton("Annuler");
            panel.Display();
        }

        // ---------------------------- Historique ----------------------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowHistory(Player player, List<Actualisation> history)
        {
            Panel panel = ctx.PanelHelper.Create("France Travail - Mes actualisations", UIPanel.PanelType.Tab, player, () => ShowHistory(player, history));

            if (history.Count == 0)
                panel.AddTabLine("Aucune actualisation", _ => { });
            foreach (Actualisation a in history)
            {
                panel.AddTabLine($"{FtTime.Format(a.CreatedAt)} - {a.StatusLabel()}", _ => ShowActualisation(player, a));
            }

            panel.AddButton("Détails", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowActualisation(Player player, Actualisation a)
        {
            Panel panel = ctx.PanelHelper.Create($"Actualisation du {FtTime.Format(a.CreatedAt)}", UIPanel.PanelType.Text, player, () => ShowActualisation(player, a));
            panel.TextLines.AddRange(a.Summary());
            panel.TextLines.Add($"{Bold("Statut :")} {a.StatusLabel()}{(string.IsNullOrEmpty(a.ProcessedBy) ? "" : $" par {a.ProcessedBy}")}");
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---------------------------- Propositions ----------------------------

        public async void OpenProposals(Player player)
        {
            int characterId = player.character.Id;
            List<JobProposal> proposals = await JobProposal.Query(p => p.CharacterId == characterId);
            ShowProposals(player, proposals.OrderByDescending(p => p.CreatedAt).ToList());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowProposals(Player player, List<JobProposal> proposals)
        {
            Panel panel = ctx.PanelHelper.Create("France Travail - Mes propositions d'emploi", UIPanel.PanelType.Tab, player, () => OpenProposals(player));
            Live.Track("Joueur." + nameof(ShowProposals), panel);

            if (proposals.Count == 0)
                panel.AddTabLine("Aucune proposition pour le moment", _ => { });
            foreach (JobProposal p in proposals)
            {
                panel.AddTabLine($"{p.JobTitle} - {p.Employer} ({p.StatusLabel()})", _ => { Live.Leave(panel); ShowProposal(player, p); });
            }

            panel.NextButton("Détails", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowProposal(Player player, JobProposal p)
        {
            Panel panel = ctx.PanelHelper.Create("Proposition d'emploi", UIPanel.PanelType.Text, player, () => ShowProposal(player, p));
            panel.TextLines.AddRange(p.Summary());

            if (p.Status == (int)ProposalStatus.EnAttente)
            {
                panel.PreviousButtonWithAction(Color("Accepter", Colors.Success), async () => await Answer(player, p, true));
                panel.PreviousButtonWithAction(Color("Refuser", Colors.Error), async () => await Answer(player, p, false));
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> Answer(Player player, JobProposal p, bool accepted)
        {
            p.Status = (int)(accepted ? ProposalStatus.Acceptee : ProposalStatus.Refusee);
            p.AnsweredAt = FtTime.Now();
            if (!await p.Save())
            {
                player.Notify(FranceTravailPlugin.ActivityName, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
                return false;
            }

            player.Notify(FranceTravailPlugin.ActivityName,
                accepted ? $"Vous avez accepté le poste « {p.JobTitle} ». Contactez {p.Employer} !" : "Proposition refusée.",
                accepted ? NotificationManager.Type.Success : NotificationManager.Type.Info, 6f);
            await ctx.NotifyAgencies($"{p.PlayerName} a {(accepted ? "accepté" : "refusé")} le poste « {p.JobTitle} » ({p.Employer}).",
                accepted ? NotificationManager.Type.Success : NotificationManager.Type.Warning);
            Live.RefreshAll();
            return true;
        }

        public static string Clean(string text, int maxLength)
        {
            string value = Nova.RemoveTextFormat(text ?? "").Trim();
            if (value.Length == 0) return null;
            return value.Length > maxLength ? value.Substring(0, maxLength) : value;
        }
    }

    // ======================================================================
    //  Côté entreprise : espace patron (temps réel)
    // ======================================================================

    public class AgencyMenu
    {
        private readonly FranceTravailPlugin ctx;

        public AgencyMenu(FranceTravailPlugin context)
        {
            ctx = context;
        }

        /// <summary>Vérifie l'accès ; les vues sont rechargées avec ce contrôle à chaque rafraîchissement.</summary>
        private async Task<bool> CheckAccess(Player player)
        {
            if (player?.character == null) return false;
            if (!await ctx.IsAgencyMember(player))
            {
                player.Notify(FranceTravailPlugin.ActivityName, "Votre entreprise n'est pas une agence France Travail.", NotificationManager.Type.Error, 5f);
                return false;
            }
            if (!await ctx.CanManage(player))
            {
                player.Notify(FranceTravailPlugin.ActivityName, "L'espace patron est réservé au patron et aux gestionnaires.", NotificationManager.Type.Error, 5f);
                return false;
            }
            return true;
        }

        public async void Open(Player player)
        {
            if (!await CheckAccess(player)) return;
            List<Actualisation> all = await Actualisation.QueryAll();
            List<JobProposal> proposals = await JobProposal.QueryAll();
            ShowHome(player, all, proposals);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowHome(Player player, List<Actualisation> all, List<JobProposal> proposals)
        {
            Panel panel = ctx.PanelHelper.Create($"France Travail - {player.biz.BizName}", UIPanel.PanelType.Tab, player, () => Open(player));
            Live.Track("Agence." + nameof(ShowHome), panel);

            int pending = all.Count(a => a.Status == (int)ActualisationStatus.EnAttente);
            int seekers = LatestBySeeker(all).Count(a => a.IsSearching);
            int waitingAnswers = proposals.Count(p => p.Status == (int)ProposalStatus.EnAttente);

            panel.AddTabLine(pending > 0 ? Color($"Actualisations à traiter ({pending})", Colors.Warning) : "Actualisations à traiter (0)",
                _ => { Live.Leave(panel); OpenPending(player); });
            panel.AddTabLine($"Demandeurs d'emploi ({seekers})", _ => { Live.Leave(panel); OpenSeekers(player); });
            panel.AddTabLine($"Propositions envoyées ({waitingAnswers} en attente de réponse)", _ => { Live.Leave(panel); OpenProposals(player); });
            panel.AddTabLine("Historique des actualisations", _ => { Live.Leave(panel); OpenHistory(player); });

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Dernière actualisation de chaque demandeur.</summary>
        private static List<Actualisation> LatestBySeeker(List<Actualisation> all)
        {
            return all.GroupBy(a => a.CharacterId)
                .Select(g => g.OrderByDescending(a => a.CreatedAt).First())
                .OrderByDescending(a => a.CreatedAt)
                .ToList();
        }

        // ---------------------------- Actualisations ----------------------------

        public async void OpenPending(Player player)
        {
            if (!await CheckAccess(player)) return;
            List<Actualisation> pending = await Actualisation.Query(a => a.Status == (int)ActualisationStatus.EnAttente);
            ShowPending(player, pending.OrderBy(a => a.CreatedAt).ToList());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowPending(Player player, List<Actualisation> pending)
        {
            Panel panel = ctx.PanelHelper.Create("France Travail - Actualisations à traiter", UIPanel.PanelType.Tab, player, () => OpenPending(player));
            Live.Track("Agence." + nameof(ShowPending), panel);

            if (pending.Count == 0)
                panel.AddTabLine("Aucune actualisation en attente", _ => { });
            foreach (Actualisation a in pending)
            {
                panel.AddTabLine($"{FtTime.Format(a.CreatedAt)} - {a.PlayerName} - {a.SearchLabel()}", _ => { Live.Leave(panel); ShowActualisation(player, a); });
            }

            panel.NextButton("Traiter", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public async void OpenHistory(Player player)
        {
            if (!await CheckAccess(player)) return;
            List<Actualisation> all = await Actualisation.QueryAll();
            ShowHistory(player, all.OrderByDescending(a => a.CreatedAt).Take(100).ToList());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowHistory(Player player, List<Actualisation> list)
        {
            Panel panel = ctx.PanelHelper.Create("France Travail - Historique (100 dernières)", UIPanel.PanelType.Tab, player, () => OpenHistory(player));
            Live.Track("Agence." + nameof(ShowHistory), panel);

            if (list.Count == 0)
                panel.AddTabLine("Aucune actualisation", _ => { });
            foreach (Actualisation a in list)
            {
                panel.AddTabLine($"{FtTime.Format(a.CreatedAt)} - {a.PlayerName} - {a.StatusLabel()}", _ => { Live.Leave(panel); ShowActualisation(player, a); });
            }

            panel.NextButton("Détails", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowActualisation(Player player, Actualisation a)
        {
            Panel panel = ctx.PanelHelper.Create($"Actualisation de {a.PlayerName}", UIPanel.PanelType.Text, player, () => ShowActualisation(player, a));
            panel.TextLines.Add($"{Bold("Envoyée le :")} {FtTime.Format(a.CreatedAt)}");
            panel.TextLines.AddRange(a.Summary());
            panel.TextLines.Add($"{Bold("Statut :")} {a.StatusLabel()}{(string.IsNullOrEmpty(a.ProcessedBy) ? "" : $" par {a.ProcessedBy} le {FtTime.Format(a.ProcessedAt)}")}");

            if (a.Status == (int)ActualisationStatus.EnAttente)
            {
                panel.PreviousButtonWithAction(Color("Valider", Colors.Success), async () => await Process(player, a, true));
                panel.PreviousButtonWithAction(Color("Refuser", Colors.Error), async () => await Process(player, a, false));
            }
            if (a.IsSearching)
                panel.AddButton("Proposer un emploi", _ => AskJobTitle(player, NewProposal(player, a)));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> Process(Player player, Actualisation a, bool validated)
        {
            // Un autre conseiller a pu la traiter entre-temps
            Actualisation current = await Actualisation.Query(a.Id);
            if (current != null && current.Status != (int)ActualisationStatus.EnAttente)
            {
                player.Notify(FranceTravailPlugin.ActivityName, $"Déjà traitée par {current.ProcessedBy}.", NotificationManager.Type.Warning, 5f);
                return true;
            }

            a.Status = (int)(validated ? ActualisationStatus.Validee : ActualisationStatus.Refusee);
            a.ProcessedBy = player.FullName;
            a.ProcessedAt = FtTime.Now();
            if (!await a.Save())
            {
                player.Notify(FranceTravailPlugin.ActivityName, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
                return false;
            }

            player.Notify(FranceTravailPlugin.ActivityName, $"Actualisation de {a.PlayerName} {(validated ? "validée" : "refusée")}.", NotificationManager.Type.Success, 5f);
            FranceTravailPlugin.FindOnlinePlayer(a.CharacterId)?.Notify(FranceTravailPlugin.ActivityName,
                validated ? "Votre actualisation a été validée par votre conseiller." : "Votre actualisation a été refusée. Contactez votre conseiller.",
                validated ? NotificationManager.Type.Success : NotificationManager.Type.Warning, 8f);
            Live.RefreshAll();
            return true;
        }

        // ---------------------------- Demandeurs ----------------------------

        public async void OpenSeekers(Player player)
        {
            if (!await CheckAccess(player)) return;
            List<Actualisation> seekers = LatestBySeeker(await Actualisation.QueryAll()).Where(a => a.IsSearching).ToList();
            ShowSeekers(player, seekers);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowSeekers(Player player, List<Actualisation> seekers)
        {
            Panel panel = ctx.PanelHelper.Create("France Travail - Demandeurs d'emploi", UIPanel.PanelType.Tab, player, () => OpenSeekers(player));
            Live.Track("Agence." + nameof(ShowSeekers), panel);

            if (seekers.Count == 0)
                panel.AddTabLine("Aucun demandeur d'emploi", _ => { });
            foreach (Actualisation a in seekers)
            {
                string available = a.Available ? Color("disponible", Colors.Success) : Color("pas disponible", Colors.Grey);
                panel.AddTabLine($"{a.PlayerName} - {a.Sector} - {available}", _ => { Live.Leave(panel); OpenSeeker(player, a); });
            }

            panel.NextButton("Voir le dossier", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void OpenSeeker(Player player, Actualisation last)
        {
            int characterId = last.CharacterId;
            List<Actualisation> history = await Actualisation.Query(a => a.CharacterId == characterId);
            List<JobProposal> proposals = await JobProposal.Query(p => p.CharacterId == characterId);
            ShowSeeker(player, last, history.Count, proposals);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowSeeker(Player player, Actualisation last, int actualisations, List<JobProposal> proposals)
        {
            Panel panel = ctx.PanelHelper.Create($"Dossier de {last.PlayerName}", UIPanel.PanelType.Text, player, () => OpenSeeker(player, last));
            bool online = FranceTravailPlugin.FindOnlinePlayer(last.CharacterId) != null;

            panel.TextLines.Add($"{Bold("Connecté :")} {(online ? Color("Oui", Colors.Success) : Color("Non", Colors.Grey))}");
            panel.TextLines.Add($"{Bold("Actualisations :")} {actualisations}, dernière le {FtTime.Format(last.CreatedAt)}");
            panel.TextLines.AddRange(last.Summary());
            panel.TextLines.Add($"{Bold("Propositions reçues :")} {proposals.Count} " +
                $"(acceptées : {proposals.Count(p => p.Status == (int)ProposalStatus.Acceptee)}, " +
                $"refusées : {proposals.Count(p => p.Status == (int)ProposalStatus.Refusee)})");

            panel.AddButton("Proposer un emploi", _ => AskJobTitle(player, NewProposal(player, last)));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---------------------------- Proposer un emploi ----------------------------

        private static JobProposal NewProposal(Player agent, Actualisation seeker)
        {
            return new JobProposal
            {
                CharacterId = seeker.CharacterId,
                PlayerName = seeker.PlayerName,
                AgencyBizId = agent.biz.Id,
                AgencyName = agent.biz.BizName,
                ProposedBy = agent.FullName
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskJobTitle(Player player, JobProposal p)
        {
            Panel panel = ctx.PanelHelper.Create($"Proposition pour {p.PlayerName} - Poste", UIPanel.PanelType.Input, player, () => AskJobTitle(player, p));
            panel.TextLines.Add("Intitulé du poste proposé");
            panel.SetInputPlaceholder("Ex : Livreur, Vendeur, Agent de sécurité...");
            panel.AddButton("Suivant", _ =>
            {
                string title = PlayerMenu.Clean(panel.inputText, 60);
                if (title == null)
                {
                    player.Notify(FranceTravailPlugin.ActivityName, "L'intitulé du poste est obligatoire.", NotificationManager.Type.Error, 5f);
                    return;
                }
                p.JobTitle = title;
                AskEmployer(player, p);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskEmployer(Player player, JobProposal p)
        {
            Panel panel = ctx.PanelHelper.Create($"Proposition pour {p.PlayerName} - Entreprise", UIPanel.PanelType.Tab, player, () => AskEmployer(player, p));

            panel.AddTabLine(Color("Autre (saisir le nom)", Colors.Info), _ => AskEmployerName(player, p));
            foreach (Bizs biz in Nova.biz.bizs.OrderBy(b => b.BizName))
            {
                string name = BizUtils.BizNameWithoutId(biz.BizName);
                panel.AddTabLine(name, _ => { p.Employer = name; AskDetails(player, p); });
            }

            panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskEmployerName(Player player, JobProposal p)
        {
            Panel panel = ctx.PanelHelper.Create($"Proposition pour {p.PlayerName} - Entreprise", UIPanel.PanelType.Input, player, () => AskEmployerName(player, p));
            panel.TextLines.Add("Nom de l'entreprise qui recrute");
            panel.SetInputPlaceholder("Nom de l'entreprise");
            panel.AddButton("Suivant", _ =>
            {
                string employer = PlayerMenu.Clean(panel.inputText, 60);
                if (employer == null)
                {
                    player.Notify(FranceTravailPlugin.ActivityName, "Le nom de l'entreprise est obligatoire.", NotificationManager.Type.Error, 5f);
                    return;
                }
                p.Employer = employer;
                AskDetails(player, p);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskDetails(Player player, JobProposal p)
        {
            Panel panel = ctx.PanelHelper.Create($"Proposition pour {p.PlayerName} - Détails", UIPanel.PanelType.Input, player, () => AskDetails(player, p));
            panel.TextLines.Add("Détails : salaire, horaires, personne à contacter... (facultatif)");
            panel.SetInputPlaceholder("Ex : 1500€, du lundi au vendredi, contacter M. Dupont");
            panel.AddButton("Suivant", _ => { p.Details = PlayerMenu.Clean(panel.inputText, 200); ConfirmProposal(player, p); });
            panel.AddButton("Passer", _ => { p.Details = null; ConfirmProposal(player, p); });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ConfirmProposal(Player player, JobProposal p)
        {
            Panel panel = ctx.PanelHelper.Create($"Proposition pour {p.PlayerName}", UIPanel.PanelType.Text, player, () => ConfirmProposal(player, p));
            panel.TextLines.Add($"{Bold("Demandeur :")} {p.PlayerName}");
            panel.TextLines.Add($"{Bold("Poste :")} {p.JobTitle}");
            panel.TextLines.Add($"{Bold("Entreprise :")} {p.Employer}");
            if (!string.IsNullOrEmpty(p.Details))
                panel.TextLines.Add($"{Bold("Détails :")} {p.Details}");

            panel.CloseButtonWithAction(Color("Envoyer", Colors.Success), async () => await SendProposal(player, p));
            panel.PreviousButton("Modifier");
            panel.CloseButton("Annuler");
            panel.Display();
        }

        private async Task<bool> SendProposal(Player player, JobProposal p)
        {
            p.CreatedAt = FtTime.Now();
            p.Status = (int)ProposalStatus.EnAttente;
            if (!await p.Save())
            {
                player.Notify(FranceTravailPlugin.ActivityName, "Erreur lors de l'envoi.", NotificationManager.Type.Error, 5f);
                return false;
            }

            player.Notify(FranceTravailPlugin.ActivityName, $"Proposition envoyée à {p.PlayerName}.", NotificationManager.Type.Success, 5f);
            FranceTravailPlugin.FindOnlinePlayer(p.CharacterId)?.Notify(FranceTravailPlugin.ActivityName,
                $"Nouvelle proposition d'emploi : {p.JobTitle} chez {p.Employer}. Consultez /francetravail.", NotificationManager.Type.Info, 10f);
            Live.RefreshAll();
            return true;
        }

        // ---------------------------- Propositions envoyées ----------------------------

        public async void OpenProposals(Player player)
        {
            if (!await CheckAccess(player)) return;
            List<JobProposal> proposals = await JobProposal.QueryAll();
            ShowProposals(player, proposals.OrderByDescending(p => p.CreatedAt).Take(100).ToList());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowProposals(Player player, List<JobProposal> proposals)
        {
            Panel panel = ctx.PanelHelper.Create("France Travail - Propositions envoyées", UIPanel.PanelType.Tab, player, () => OpenProposals(player));
            Live.Track("Agence." + nameof(ShowProposals), panel);

            if (proposals.Count == 0)
                panel.AddTabLine("Aucune proposition envoyée", _ => { });
            foreach (JobProposal p in proposals)
            {
                panel.AddTabLine($"{p.PlayerName} - {p.JobTitle} ({p.Employer}) - {p.StatusLabel()}", _ => { Live.Leave(panel); ShowProposal(player, p); });
            }

            panel.NextButton("Détails", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowProposal(Player player, JobProposal p)
        {
            Panel panel = ctx.PanelHelper.Create($"Proposition pour {p.PlayerName}", UIPanel.PanelType.Text, player, () => ShowProposal(player, p));
            panel.TextLines.Add($"{Bold("Demandeur :")} {p.PlayerName}");
            panel.TextLines.AddRange(p.Summary());
            if (p.AnsweredAt > 0)
                panel.TextLines.Add($"{Bold("Réponse le :")} {FtTime.Format(p.AnsweredAt)}");

            panel.PreviousButtonWithAction(Color("Supprimer", Colors.Error), async () =>
            {
                if (await p.Delete())
                {
                    Live.RefreshAll();
                    return true;
                }
                player.Notify(FranceTravailPlugin.ActivityName, "Erreur lors de la suppression.", NotificationManager.Type.Error, 5f);
                return false;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }
}
