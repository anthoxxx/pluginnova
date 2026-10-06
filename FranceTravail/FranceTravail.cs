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
using Mirror;
using ModKit.Helper;
using ModKit.Helper.JobHelper;
using ModKit.Helper.PointHelper;
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
    /// France Travail : les joueurs s'inscrivent puis s'actualisent (questionnaires), et les
    /// employés des agences France Travail traitent inscriptions et actualisations en temps réel
    /// depuis un point bleu placé par le staff, et proposent des emplois aux demandeurs.
    /// </summary>
    public class FranceTravailPlugin : ModKit.ModKit
    {
        /// <summary>Nom de l'activité personnalisée AAMenu des entreprises France Travail.</summary>
        public const string ActivityName = "France Travail";

        public static FranceTravailPlugin Instance { get; private set; }

        public FranceTravailConfig Config { get; private set; }
        public PlayerMenu PlayerMenu { get; private set; }
        public AgencyMenu AgencyMenu { get; private set; }
        public RsaService Rsa { get; private set; }

        public FranceTravailPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.2.0", "anthoxxx");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            Instance = this;

            Config = FranceTravailConfig.Load(Path.Combine(pluginsPath, "FranceTravail"));

            Orm.RegisterTable<JobSeeker>();
            Orm.RegisterTable<Actualisation>();
            Orm.RegisterTable<JobProposal>();
            Orm.RegisterTable<FranceTravailPoint>();

            PlayerMenu = new PlayerMenu(this);
            AgencyMenu = new AgencyMenu(this);

            InitActivity();

            Rsa = new RsaService(this);
            Nova.server.OnMinutePassedEvent += Rsa.OnMinutePassed;

            // Points bleus : AAMenu > Administration > Points bleus > France Travail
            FranceTravailPoint pattern = new FranceTravailPoint(false) { Context = this };
            PointHelper.AddPattern(nameof(FranceTravailPoint), pattern);

            if (AAMenu.AAMenu.menu != null)
            {
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, "France Travail", pattern, this);

                // Accès joueur aussi depuis AAMenu > Interactions
                AAMenu.Menu.AddInteractionTabLine(PluginInformations, "France Travail",
                    ui => PlayerMenu.Open(PanelHelper.ReturnPlayerFromPanel(ui)));
                // Accès agence aussi depuis AAMenu > Métier (sociétés France Travail uniquement)
                AAMenu.Menu.AddBizTabLine(PluginInformations, null, new CustomActivity { Name = ActivityName },
                    "France Travail - Espace agence", ui => AgencyMenu.Open(PanelHelper.ReturnPlayerFromPanel(ui)));
            }
            else
            {
                Logger.LogError(PluginInformations.SourceName, "AAMenu introuvable : impossible de placer les points France Travail.");
            }

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", "initialisé");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            PointHelper.InitAllNPoint(player);
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

        /// <summary>Déclare une entreprise comme agence France Travail (si ce n'est pas déjà le cas).</summary>
        public async Task<bool> LinkAgency(int bizId)
        {
            if ((await GetAgencyBizIds()).Contains(bizId)) return true;
            CustomActivity activity = await GetActivity();
            return activity != null && await JobHelper.AddCustomBiz(bizId, activity.Id);
        }

        public async Task<bool> IsAgencyMember(Player player)
        {
            return player.HasBiz && (await GetAgencyBizIds()).Contains(player.biz.Id);
        }

        /// <summary>Accès à l'espace agence : tous les employés, ou seulement patron et gestionnaires si configuré.</summary>
        public async Task<bool> CanManage(Player player)
        {
            if (!await IsAgencyMember(player)) return false;
            if (!Config.EspaceAgenceReserveAuxPatrons || PermissionUtils.PlayerIsOwner(player)) return true;
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

        public static void Notify(Player player, string message, NotificationManager.Type type, float duration = 5f)
        {
            player?.Notify(ActivityName, message, type, duration);
        }
    }

    // ======================================================================
    //  Configuration (Plugins/FranceTravail/config.json)
    // ======================================================================

    public class FranceTravailConfig
    {
        /// <summary>Délai minimum (en heures réelles) entre deux actualisations d'un même joueur.</summary>
        public int DelaiEntreActualisationsHeures { get; set; } = 24;

        /// <summary>Montant du RSA versé à chaque paiement.</summary>
        public double MontantRsa { get; set; } = 500;

        /// <summary>Intervalle entre deux paiements du RSA, en minutes réelles.</summary>
        public int IntervallePaiementRsaMinutes { get; set; } = 15;

        /// <summary>
        /// Durée (heures réelles) pendant laquelle une inscription ou une actualisation ouvre droit
        /// au RSA. Passé ce délai sans nouvelle actualisation, le RSA n'est plus versé.
        /// </summary>
        public int ValiditeActualisationHeures { get; set; } = 24;

        /// <summary>true : le RSA n'est versé qu'une fois l'inscription validée par un conseiller.</summary>
        public bool RsaApresValidationSeulement { get; set; } = false;

        /// <summary>true : RSA versé sur le compte en banque ; false : en liquide.</summary>
        public bool RsaSurCompteBancaire { get; set; } = true;

        /// <summary>false : tous les employés de l'agence ont l'espace agence. true : seulement patron et gestionnaires.</summary>
        public bool EspaceAgenceReserveAuxPatrons { get; set; } = false;

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
                FranceTravailConfig config = File.Exists(path)
                    ? JsonConvert.DeserializeObject<FranceTravailConfig>(File.ReadAllText(path))
                    : null;
                config = config ?? new FranceTravailConfig();
                if (config.Secteurs == null || config.Secteurs.Count == 0) config.Secteurs = new FranceTravailConfig().Secteurs;
                // Réécrit le fichier pour y ajouter les nouvelles clés éventuelles
                File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
                return config;
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

    public enum RequestStatus { EnAttente = 0, Validee = 1, Refusee = 2 }
    public enum SearchStatus { Recherche = 0, EmploiTrouve = 1, NeRecherchePlus = 2 }

    public static class Labels
    {
        public static string Status(int status)
        {
            switch ((RequestStatus)status)
            {
                case RequestStatus.Validee: return Color("Validée", Colors.Success);
                case RequestStatus.Refusee: return Color("Refusée", Colors.Error);
                default: return Color("En attente", Colors.Warning);
            }
        }

        public static string YesNo(bool value) => value ? "Oui" : "Non";

        /// <summary>Ligne « Libellé : valeur » pour les panels de détail.</summary>
        public static string Line(string label, string value) => $"{Bold(label + " :")} {value}";
    }

    /// <summary>Inscription d'un joueur comme demandeur d'emploi.</summary>
    public class JobSeeker : ModEntity<JobSeeker>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int CharacterId { get; set; }
        public string PlayerName { get; set; }
        public string Phone { get; set; }
        public string Birthday { get; set; }
        public bool DrivingLicense { get; set; }
        public long CreatedAt { get; set; }

        public string AgeRange { get; set; }
        public string Education { get; set; }
        public string Experience { get; set; }
        public string Sector { get; set; }
        public string Contract { get; set; }
        public bool Available { get; set; }
        public string Presentation { get; set; }

        public int Status { get; set; }
        public string ProcessedBy { get; set; }
        public long ProcessedAt { get; set; }

        /// <summary>false une fois désinscrit (emploi trouvé, désinscription, radiation).</summary>
        public bool Active { get; set; }
        public string EndReason { get; set; }

        /// <summary>Total du RSA versé pendant cette inscription, et date du dernier versement.</summary>
        public double TotalRsa { get; set; }
        public long LastRsaAt { get; set; }

        public JobSeeker() { }

        public List<string> Summary()
        {
            List<string> lines = new List<string>
            {
                Labels.Line("Nom", PlayerName),
                Labels.Line("Téléphone", string.IsNullOrEmpty(Phone) ? "-" : Phone),
                Labels.Line("Âge", AgeRange),
                Labels.Line("Permis B", Labels.YesNo(DrivingLicense)),
                Labels.Line("Études", Education),
                Labels.Line("Expérience", Experience),
                Labels.Line("Secteur", Sector),
                Labels.Line("Contrat", Contract),
                Labels.Line("Disponible", Labels.YesNo(Available))
            };
            if (!string.IsNullOrEmpty(Presentation))
                lines.Add(Labels.Line("Présentation", Presentation));
            return lines;
        }
    }

    /// <summary>Une actualisation = les réponses d'un demandeur au questionnaire périodique.</summary>
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
                default: return "Ne recherche plus";
            }
        }

        public List<string> Summary()
        {
            List<string> lines = new List<string>
            {
                Labels.Line("Travaillé", HasWorked ? $"Oui, {HoursWorked}" : "Non")
            };
            if (HasWorked && !string.IsNullOrEmpty(Employer))
                lines.Add(Labels.Line("Employeur", Employer));
            lines.Add(Labels.Line("Arrêt maladie", Labels.YesNo(SickLeave)));
            lines.Add(Labels.Line("Formation", Labels.YesNo(Training)));
            lines.Add(Labels.Line("Situation", SearchLabel()));
            if (IsSearching)
                lines.Add(Labels.Line("Disponible", Labels.YesNo(Available)));
            if (!string.IsNullOrEmpty(Comment))
                lines.Add(Labels.Line("Commentaire", Comment));
            return lines;
        }
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

        /// <summary>EnAttente / Validee (= acceptée) / Refusee.</summary>
        public int Status { get; set; }
        public long AnsweredAt { get; set; }

        public JobProposal() { }

        public string StatusLabel()
        {
            switch ((RequestStatus)Status)
            {
                case RequestStatus.Validee: return Color("Acceptée", Colors.Success);
                case RequestStatus.Refusee: return Color("Refusée", Colors.Error);
                default: return Color("En attente", Colors.Warning);
            }
        }

        public List<string> Summary()
        {
            List<string> lines = new List<string>
            {
                Labels.Line("Poste", JobTitle),
                Labels.Line("Entreprise", Employer)
            };
            if (!string.IsNullOrEmpty(Details))
                lines.Add(Labels.Line("Détails", Details));
            lines.Add(Labels.Line("Conseiller", ProposedBy));
            lines.Add(Labels.Line("Envoyée le", FtTime.Format(CreatedAt)));
            lines.Add(Labels.Line("Statut", StatusLabel()));
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

    /// <summary>Briques de panels communes. Le panel doit avoir été créé par la méthode appelante.</summary>
    public static class Ui
    {
        /// <summary>
        /// Lignes d'information dans un panel Tab : la liste défile, donc le texte ne
        /// déborde jamais sur le titre ou les boutons (contrairement au panel Text).
        /// </summary>
        public static void InfoLines(Panel panel, IEnumerable<string> lines)
        {
            foreach (string line in lines)
                panel.AddTabLine(line, _ => { });
        }

        /// <summary>Question Oui / Non : panel Text avec une question courte et deux boutons.</summary>
        public static void YesNo(Panel panel, string question, string yes, Action onYes, string no, Action onNo)
        {
            panel.TextLines.Add(question);
            panel.AddButton(Color(yes, Colors.Success), _ => onYes());
            panel.AddButton(Color(no, Colors.Error), _ => onNo());
            panel.PreviousButton();
            panel.CloseButton("Annuler");
            panel.Display();
        }

        /// <summary>Question à choix : panel Tab, le titre porte la question.</summary>
        public static void Choices(Panel panel, IEnumerable<string> options, Action<string> onChoose)
        {
            foreach (string option in options)
                panel.AddTabLine(option, _ => onChoose(option));
            panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton("Annuler");
            panel.Display();
        }

        /// <summary>Saisie libre facultative : panel Input avec Valider / Passer.</summary>
        public static void OptionalInput(Panel panel, string question, string placeholder, int maxLength, Action<string> onDone)
        {
            panel.TextLines.Add(question);
            panel.SetInputPlaceholder(placeholder);
            panel.AddButton("Valider", _ => onDone(Clean(panel.inputText, maxLength)));
            panel.AddButton("Passer", _ => onDone(null));
            panel.PreviousButton();
            panel.CloseButton("Annuler");
            panel.Display();
        }

        public static string Clean(string text, int maxLength)
        {
            string value = Nova.RemoveTextFormat(text ?? "").Trim();
            if (value.Length == 0) return null;
            return value.Length > maxLength ? value.Substring(0, maxLength) : value;
        }
    }

    // ======================================================================
    //  RSA : versement automatique aux inscrits à jour de leurs actualisations
    // ======================================================================

    public enum RsaState { Verse, InscriptionRefusee, ValidationRequise, ActualisationRequise }

    public class RsaService
    {
        private readonly FranceTravailPlugin ctx;
        private long lastPaymentAt;
        private bool paying;

        public RsaService(FranceTravailPlugin context)
        {
            ctx = context;
        }

        private FranceTravailConfig Config => ctx.Config;

        /// <summary>
        /// Appelé à chaque minute du serveur. Le paiement est cadencé sur l'heure réelle,
        /// pour tomber toutes les N minutes quelle que soit la vitesse de l'heure en jeu.
        /// </summary>
        public void OnMinutePassed()
        {
            long now = FtTime.Now();
            if (lastPaymentAt == 0) lastPaymentAt = now; // premier paiement N minutes après le démarrage
            if (paying || now - lastPaymentAt < Math.Max(1, Config.IntervallePaiementRsaMinutes) * 60L) return;
            lastPaymentAt = now;
            PayAll();
        }

        /// <summary>
        /// Droit au RSA : inscription active et non refusée, et inscription ou dernière actualisation
        /// (non refusée) datant de moins de <see cref="FranceTravailConfig.ValiditeActualisationHeures"/>.
        /// </summary>
        public RsaState GetState(JobSeeker s, List<Actualisation> actualisations)
        {
            if (s.Status == (int)RequestStatus.Refusee) return RsaState.InscriptionRefusee;
            if (Config.RsaApresValidationSeulement && s.Status != (int)RequestStatus.Validee) return RsaState.ValidationRequise;
            return RightsUntil(s, actualisations) >= FtTime.Now() ? RsaState.Verse : RsaState.ActualisationRequise;
        }

        /// <summary>Date jusqu'à laquelle l'inscription ou la dernière actualisation ouvre droit au RSA.</summary>
        public long RightsUntil(JobSeeker s, List<Actualisation> actualisations)
        {
            long reference = s.CreatedAt;
            foreach (Actualisation a in actualisations)
            {
                if (a.CharacterId == s.CharacterId && a.Status != (int)RequestStatus.Refusee && a.CreatedAt > reference)
                    reference = a.CreatedAt;
            }
            return reference + Config.ValiditeActualisationHeures * 3600L;
        }

        /// <summary>Ligne d'état du RSA pour les menus joueur et agence.</summary>
        public string StateLine(JobSeeker s, List<Actualisation> actualisations)
        {
            string amount = $"{Config.MontantRsa:0.##}€ / {Config.IntervallePaiementRsaMinutes} min";
            switch (GetState(s, actualisations))
            {
                case RsaState.Verse:
                    return Labels.Line("RSA", Color($"versé ({amount}) jusqu'au {FtTime.Format(RightsUntil(s, actualisations))}", Colors.Success));
                case RsaState.ValidationRequise:
                    return Labels.Line("RSA", Color("en attente de validation", Colors.Warning));
                case RsaState.ActualisationRequise:
                    return Labels.Line("RSA", Color("suspendu, actualisation requise", Colors.Error));
                default:
                    return Labels.Line("RSA", Color("non versé", Colors.Error));
            }
        }

        private async void PayAll()
        {
            paying = true;
            try
            {
                List<Player> online = Nova.server.Players.Where(p => p?.character != null).ToList();
                if (online.Count == 0 || Config.MontantRsa <= 0) return;

                Dictionary<int, JobSeeker> seekers = (await JobSeeker.Query(s => s.Active))
                    .GroupBy(s => s.CharacterId)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.CreatedAt).First());

                foreach (Player player in online)
                {
                    int characterId = player.character.Id;
                    if (!seekers.TryGetValue(characterId, out JobSeeker seeker)) continue;

                    List<Actualisation> actualisations = await Actualisation.Query(a => a.CharacterId == characterId);
                    RsaState state = GetState(seeker, actualisations);

                    if (state == RsaState.Verse)
                    {
                        if (Config.RsaSurCompteBancaire)
                            player.AddBankMoney(Config.MontantRsa, "RSA France Travail");
                        else
                            player.AddMoney(Config.MontantRsa, "RSA France Travail");

                        seeker.TotalRsa += Config.MontantRsa;
                        seeker.LastRsaAt = FtTime.Now();
                        await seeker.Save();

                        FranceTravailPlugin.Notify(player, $"Vous avez reçu votre RSA : {Config.MontantRsa:0.##}€", NotificationManager.Type.Success, 8f);
                    }
                    else if (state == RsaState.ActualisationRequise)
                    {
                        FranceTravailPlugin.Notify(player, "RSA non versé : vous ne vous êtes pas actualisé.", NotificationManager.Type.Warning, 8f);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError("FranceTravail", $"Paiement du RSA : {e.Message}");
            }
            finally
            {
                paying = false;
            }
        }
    }

    // ======================================================================
    //  Côté joueur : inscription, actualisation, propositions d'emploi
    // ======================================================================

    public class PlayerMenu
    {
        private static readonly string[] AgeRanges = { "18 - 25 ans", "26 - 35 ans", "36 - 50 ans", "Plus de 50 ans" };
        private static readonly string[] EducationLevels = { "Sans diplôme", "CAP / BEP", "Bac", "Bac +2", "Bac +3 et plus" };
        private static readonly string[] ExperienceLevels = { "Aucune expérience", "Moins d'1 an", "1 à 3 ans", "Plus de 3 ans" };
        private static readonly string[] Contracts = { "Temps plein", "Temps partiel", "Intérim / missions", "Peu importe" };
        private static readonly string[] HoursRanges = { "Moins de 10 heures", "10 à 20 heures", "20 à 35 heures", "Plus de 35 heures" };

        private readonly FranceTravailPlugin ctx;

        public PlayerMenu(FranceTravailPlugin context)
        {
            ctx = context;
        }

        public static async Task<JobSeeker> GetActiveSeeker(int characterId)
        {
            return (await JobSeeker.Query(s => s.CharacterId == characterId && s.Active))
                .OrderByDescending(s => s.CreatedAt).FirstOrDefault();
        }

        public async void Open(Player player)
        {
            if (player?.character == null) return;
            int characterId = player.character.Id;
            JobSeeker seeker = await GetActiveSeeker(characterId);
            List<Actualisation> history = await Actualisation.Query(a => a.CharacterId == characterId);
            List<JobProposal> proposals = await JobProposal.Query(p => p.CharacterId == characterId);
            ShowHome(player, seeker, history.OrderByDescending(a => a.CreatedAt).ToList(), proposals.OrderByDescending(p => p.CreatedAt).ToList());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowHome(Player player, JobSeeker seeker, List<Actualisation> history, List<JobProposal> proposals)
        {
            Panel panel = ctx.PanelHelper.Create("France Travail", UIPanel.PanelType.Tab, player, () => Open(player));
            Live.Track("Joueur.Accueil", panel);

            if (seeker == null || seeker.Status == (int)RequestStatus.Refusee)
            {
                if (seeker != null)
                    panel.AddTabLine(Color("Votre inscription a été refusée", Colors.Error), _ => { });
                panel.AddTabLine(Color("S'inscrire comme demandeur d'emploi", Colors.Success), _ => { Live.Leave(panel); StartRegistration(player); });
            }
            else
            {
                panel.AddTabLine($"Mon inscription : {Labels.Status(seeker.Status)}", _ => { Live.Leave(panel); ShowMyFile(player, seeker); });
                panel.AddTabLine(ctx.Rsa.StateLine(seeker, history), _ => { });

                Actualisation last = history.FirstOrDefault();
                long remaining = last == null ? 0 : last.CreatedAt + ctx.Config.DelaiEntreActualisationsHeures * 3600L - FtTime.Now();
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
                    panel.AddTabLine(Color($"Prochaine actualisation dans {remaining / 3600}h{remaining % 3600 / 60:00}", Colors.Grey), _ => { });
                }
            }

            panel.AddTabLine($"Mes actualisations ({history.Count})", _ => { Live.Leave(panel); ShowHistory(player, history); });
            int pending = proposals.Count(p => p.Status == (int)RequestStatus.EnAttente);
            panel.AddTabLine(pending > 0
                    ? Color($"Mes offres d'emploi ({pending} nouvelle(s))", Colors.Warning)
                    : $"Mes offres d'emploi ({proposals.Count})",
                _ => { Live.Leave(panel); OpenProposals(player); });

            panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        // ---------------------------- Inscription ----------------------------

        private void StartRegistration(Player player)
        {
            Characters c = player.character;
            AskAge(player, new JobSeeker
            {
                CharacterId = c.Id,
                PlayerName = player.FullName,
                Phone = c.PhoneNumber,
                Birthday = c.Birthday,
                DrivingLicense = c.PermisB
            });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskAge(Player player, JobSeeker s)
        {
            Panel panel = ctx.PanelHelper.Create("Inscription 1/7 : votre âge", UIPanel.PanelType.Tab, player, () => AskAge(player, s));
            Ui.Choices(panel, AgeRanges, v => { s.AgeRange = v; AskEducation(player, s); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskEducation(Player player, JobSeeker s)
        {
            Panel panel = ctx.PanelHelper.Create("Inscription 2/7 : vos études", UIPanel.PanelType.Tab, player, () => AskEducation(player, s));
            Ui.Choices(panel, EducationLevels, v => { s.Education = v; AskExperience(player, s); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskExperience(Player player, JobSeeker s)
        {
            Panel panel = ctx.PanelHelper.Create("Inscription 3/7 : expérience", UIPanel.PanelType.Tab, player, () => AskExperience(player, s));
            Ui.Choices(panel, ExperienceLevels, v => { s.Experience = v; AskRegSector(player, s); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskRegSector(Player player, JobSeeker s)
        {
            Panel panel = ctx.PanelHelper.Create("Inscription 4/7 : secteur", UIPanel.PanelType.Tab, player, () => AskRegSector(player, s));
            Ui.Choices(panel, ctx.Config.Secteurs, v => { s.Sector = v; AskContract(player, s); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskContract(Player player, JobSeeker s)
        {
            Panel panel = ctx.PanelHelper.Create("Inscription 5/7 : contrat", UIPanel.PanelType.Tab, player, () => AskContract(player, s));
            Ui.Choices(panel, Contracts, v => { s.Contract = v; AskRegAvailable(player, s); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskRegAvailable(Player player, JobSeeker s)
        {
            Panel panel = ctx.PanelHelper.Create("Inscription 6/7", UIPanel.PanelType.Text, player, () => AskRegAvailable(player, s));
            Ui.YesNo(panel, "Êtes-vous disponible immédiatement ?",
                "Oui", () => { s.Available = true; AskPresentation(player, s); },
                "Non", () => { s.Available = false; AskPresentation(player, s); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskPresentation(Player player, JobSeeker s)
        {
            Panel panel = ctx.PanelHelper.Create("Inscription 7/7", UIPanel.PanelType.Input, player, () => AskPresentation(player, s));
            Ui.OptionalInput(panel, "Présentez-vous en quelques mots (facultatif)", "Ex : sérieux, motivé, je cherche un poste de nuit", 200,
                v => { s.Presentation = v; ShowRegistrationRecap(player, s); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowRegistrationRecap(Player player, JobSeeker s)
        {
            Panel panel = ctx.PanelHelper.Create("Inscription : récapitulatif", UIPanel.PanelType.Tab, player, () => ShowRegistrationRecap(player, s));
            Ui.InfoLines(panel, s.Summary());
            panel.CloseButtonWithAction(Color("Envoyer", Colors.Success), async () => await SubmitRegistration(player, s));
            panel.PreviousButton("Modifier");
            panel.CloseButton("Annuler");
            panel.Display();
        }

        private async Task<bool> SubmitRegistration(Player player, JobSeeker s)
        {
            int characterId = s.CharacterId;
            // Une seule inscription active par personnage (l'ancienne, refusée, est archivée)
            foreach (JobSeeker old in await JobSeeker.Query(x => x.CharacterId == characterId && x.Active))
            {
                if (old.Status != (int)RequestStatus.Refusee)
                {
                    FranceTravailPlugin.Notify(player, "Vous êtes déjà inscrit.", NotificationManager.Type.Error);
                    return true;
                }
                old.Active = false;
                old.EndReason = "Réinscription";
                await old.Save();
            }

            s.CreatedAt = FtTime.Now();
            s.Status = (int)RequestStatus.EnAttente;
            s.Active = true;
            if (!await s.Save())
            {
                FranceTravailPlugin.Notify(player, "Erreur lors de l'envoi, réessayez.", NotificationManager.Type.Error);
                return false;
            }

            FranceTravailPlugin.Notify(player, "Votre inscription a été envoyée. Un conseiller va l'étudier.", NotificationManager.Type.Success, 6f);
            await ctx.NotifyAgencies($"Nouvelle inscription : {s.PlayerName}.", NotificationManager.Type.Info);
            Live.RefreshAll();
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowMyFile(Player player, JobSeeker s)
        {
            Panel panel = ctx.PanelHelper.Create("Mon inscription", UIPanel.PanelType.Tab, player, () => ShowMyFile(player, s));
            panel.AddTabLine(Labels.Line("Statut", Labels.Status(s.Status)), _ => { });
            panel.AddTabLine(Labels.Line("Inscrit le", FtTime.Format(s.CreatedAt)), _ => { });
            Ui.InfoLines(panel, s.Summary());
            panel.AddButton(Color("Me désinscrire", Colors.Error), _ => ConfirmUnregister(player, s));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ConfirmUnregister(Player player, JobSeeker s)
        {
            Panel panel = ctx.PanelHelper.Create("Désinscription", UIPanel.PanelType.Text, player, () => ConfirmUnregister(player, s));
            panel.TextLines.Add("Voulez-vous vraiment vous désinscrire de France Travail ?");
            panel.CloseButtonWithAction(Color("Confirmer", Colors.Error), async () =>
            {
                await EndRegistration(s, "Désinscription");
                FranceTravailPlugin.Notify(player, "Vous êtes désinscrit de France Travail.", NotificationManager.Type.Info);
                return true;
            });
            panel.PreviousButton();
            panel.Display();
        }

        public async Task EndRegistration(JobSeeker seeker, string reason)
        {
            // Relecture : le versement du RSA a pu modifier le dossier depuis l'ouverture du menu
            JobSeeker s = await JobSeeker.Query(seeker.Id) ?? seeker;
            s.Active = false;
            s.EndReason = reason;
            await s.Save();
            await ctx.NotifyAgencies($"{s.PlayerName} n'est plus inscrit ({reason.ToLower()}).", NotificationManager.Type.Info);
            Live.RefreshAll();
        }

        // ---------------------------- Actualisation ----------------------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskWorked(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation 1/5", UIPanel.PanelType.Text, player, () => AskWorked(player, d));
            Ui.YesNo(panel, "Avez-vous travaillé cette semaine ?",
                "Oui", () => { d.HasWorked = true; AskHours(player, d); },
                "Non", () => { d.HasWorked = false; d.HoursWorked = null; d.Employer = null; AskSickLeave(player, d); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskHours(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Combien d'heures ?", UIPanel.PanelType.Tab, player, () => AskHours(player, d));
            Ui.Choices(panel, HoursRanges, v => { d.HoursWorked = v; AskEmployer(player, d); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskEmployer(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Employeur", UIPanel.PanelType.Input, player, () => AskEmployer(player, d));
            Ui.OptionalInput(panel, "Pour quelle entreprise ? (facultatif)", "Nom de l'entreprise", 60,
                v => { d.Employer = v; AskSickLeave(player, d); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskSickLeave(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation 2/5", UIPanel.PanelType.Text, player, () => AskSickLeave(player, d));
            Ui.YesNo(panel, "Avez-vous été en arrêt maladie ?",
                "Oui", () => { d.SickLeave = true; AskTraining(player, d); },
                "Non", () => { d.SickLeave = false; AskTraining(player, d); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskTraining(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation 3/5", UIPanel.PanelType.Text, player, () => AskTraining(player, d));
            Ui.YesNo(panel, "Avez-vous suivi une formation ?",
                "Oui", () => { d.Training = true; AskSearch(player, d); },
                "Non", () => { d.Training = false; AskSearch(player, d); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskSearch(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("4/5 : cherchez-vous un emploi ?", UIPanel.PanelType.Tab, player, () => AskSearch(player, d));
            panel.AddTabLine("Oui, je recherche un emploi", _ => { d.Search = (int)SearchStatus.Recherche; AskAvailable(player, d); });
            panel.AddTabLine("Non, j'ai trouvé un emploi", _ => { d.Search = (int)SearchStatus.EmploiTrouve; d.Available = false; AskComment(player, d); });
            panel.AddTabLine("Non, je ne recherche plus", _ => { d.Search = (int)SearchStatus.NeRecherchePlus; d.Available = false; AskComment(player, d); });
            panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton("Annuler");
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskAvailable(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation 4/5", UIPanel.PanelType.Text, player, () => AskAvailable(player, d));
            Ui.YesNo(panel, "Êtes-vous disponible immédiatement ?",
                "Oui", () => { d.Available = true; AskComment(player, d); },
                "Non", () => { d.Available = false; AskComment(player, d); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskComment(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation 5/5", UIPanel.PanelType.Input, player, () => AskComment(player, d));
            Ui.OptionalInput(panel, "Un message pour votre conseiller ? (facultatif)", "Votre message", 200,
                v => { d.Comment = v; ShowRecap(player, d); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowRecap(Player player, Actualisation d)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisation : récapitulatif", UIPanel.PanelType.Tab, player, () => ShowRecap(player, d));
            Ui.InfoLines(panel, d.Summary());
            if (!d.IsSearching)
                panel.AddTabLine(Color("Vous serez désinscrit de France Travail.", Colors.Warning), _ => { });
            panel.CloseButtonWithAction(Color("Envoyer", Colors.Success), async () => await Submit(player, d));
            panel.PreviousButton("Modifier");
            panel.CloseButton("Annuler");
            panel.Display();
        }

        private async Task<bool> Submit(Player player, Actualisation d)
        {
            int characterId = d.CharacterId;
            JobSeeker seeker = await GetActiveSeeker(characterId);
            if (seeker == null || seeker.Status == (int)RequestStatus.Refusee)
            {
                FranceTravailPlugin.Notify(player, "Vous devez être inscrit pour vous actualiser.", NotificationManager.Type.Error);
                return true;
            }

            // Nouvelle vérification du délai (le menu a pu rester ouvert longtemps)
            Actualisation last = (await Actualisation.Query(a => a.CharacterId == characterId))
                .OrderByDescending(a => a.CreatedAt).FirstOrDefault();
            if (last != null && FtTime.Now() - last.CreatedAt < ctx.Config.DelaiEntreActualisationsHeures * 3600L)
            {
                FranceTravailPlugin.Notify(player, "Vous vous êtes déjà actualisé récemment.", NotificationManager.Type.Error);
                return true;
            }

            d.CreatedAt = FtTime.Now();
            d.Status = (int)RequestStatus.EnAttente;
            if (!await d.Save())
            {
                FranceTravailPlugin.Notify(player, "Erreur lors de l'envoi, réessayez.", NotificationManager.Type.Error);
                return false;
            }

            FranceTravailPlugin.Notify(player, "Votre actualisation a bien été envoyée.", NotificationManager.Type.Success, 6f);
            await ctx.NotifyAgencies($"Nouvelle actualisation : {d.PlayerName}.", NotificationManager.Type.Info);

            if (d.IsSearching)
            {
                // Mise à jour de la disponibilité du dossier
                seeker.Available = d.Available;
                await seeker.Save();
                Live.RefreshAll();
            }
            else
            {
                await EndRegistration(seeker, d.Search == (int)SearchStatus.EmploiTrouve ? "Emploi trouvé" : "Ne recherche plus");
            }
            return true;
        }

        // ---------------------------- Historique ----------------------------

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowHistory(Player player, List<Actualisation> history)
        {
            Panel panel = ctx.PanelHelper.Create("Mes actualisations", UIPanel.PanelType.Tab, player, () => ShowHistory(player, history));

            if (history.Count == 0)
                panel.AddTabLine("Aucune actualisation", _ => { });
            foreach (Actualisation a in history)
                panel.AddTabLine($"{FtTime.Format(a.CreatedAt)} - {Labels.Status(a.Status)}", _ => ShowActualisation(player, a));

            panel.AddButton("Détails", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowActualisation(Player player, Actualisation a)
        {
            Panel panel = ctx.PanelHelper.Create($"Actualisation du {FtTime.Format(a.CreatedAt)}", UIPanel.PanelType.Tab, player, () => ShowActualisation(player, a));
            panel.AddTabLine(Labels.Line("Statut", Labels.Status(a.Status)), _ => { });
            Ui.InfoLines(panel, a.Summary());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---------------------------- Offres d'emploi ----------------------------

        public async void OpenProposals(Player player)
        {
            int characterId = player.character.Id;
            List<JobProposal> proposals = await JobProposal.Query(p => p.CharacterId == characterId);
            ShowProposals(player, proposals.OrderByDescending(p => p.CreatedAt).ToList());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowProposals(Player player, List<JobProposal> proposals)
        {
            Panel panel = ctx.PanelHelper.Create("Mes offres d'emploi", UIPanel.PanelType.Tab, player, () => OpenProposals(player));
            Live.Track("Joueur.Offres", panel);

            if (proposals.Count == 0)
                panel.AddTabLine("Aucune offre pour le moment", _ => { });
            foreach (JobProposal p in proposals)
                panel.AddTabLine($"{p.JobTitle} - {p.Employer} ({p.StatusLabel()})", _ => { Live.Leave(panel); ShowProposal(player, p); });

            panel.NextButton("Détails", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowProposal(Player player, JobProposal p)
        {
            Panel panel = ctx.PanelHelper.Create("Offre d'emploi", UIPanel.PanelType.Tab, player, () => ShowProposal(player, p));
            Ui.InfoLines(panel, p.Summary());

            if (p.Status == (int)RequestStatus.EnAttente)
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
            p.Status = (int)(accepted ? RequestStatus.Validee : RequestStatus.Refusee);
            p.AnsweredAt = FtTime.Now();
            if (!await p.Save())
            {
                FranceTravailPlugin.Notify(player, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error);
                return false;
            }

            FranceTravailPlugin.Notify(player,
                accepted ? $"Offre acceptée ! Contactez {p.Employer}." : "Offre refusée.",
                accepted ? NotificationManager.Type.Success : NotificationManager.Type.Info, 6f);
            await ctx.NotifyAgencies($"{p.PlayerName} a {(accepted ? "accepté" : "refusé")} l'offre « {p.JobTitle} ».",
                accepted ? NotificationManager.Type.Success : NotificationManager.Type.Warning);
            Live.RefreshAll();
            return true;
        }
    }

    // ======================================================================
    //  Côté agence : espace des employés France Travail (temps réel)
    // ======================================================================

    public class AgencyMenu
    {
        private readonly FranceTravailPlugin ctx;

        public AgencyMenu(FranceTravailPlugin context)
        {
            ctx = context;
        }

        /// <summary>Vérifie l'accès ; refait à chaque rechargement (temps réel compris).</summary>
        private async Task<bool> CheckAccess(Player player)
        {
            if (player?.character == null) return false;
            if (!await ctx.IsAgencyMember(player))
            {
                FranceTravailPlugin.Notify(player, "Réservé aux employés de France Travail.", NotificationManager.Type.Error);
                return false;
            }
            if (!await ctx.CanManage(player))
            {
                FranceTravailPlugin.Notify(player, "Réservé au patron et aux gestionnaires.", NotificationManager.Type.Error);
                return false;
            }
            return true;
        }

        private static async Task<List<JobSeeker>> ActiveSeekers()
        {
            return await JobSeeker.Query(s => s.Active);
        }

        public async void Open(Player player)
        {
            if (!await CheckAccess(player)) return;
            List<JobSeeker> seekers = await ActiveSeekers();
            List<Actualisation> pendingActs = await Actualisation.Query(a => a.Status == (int)RequestStatus.EnAttente);
            List<JobProposal> waiting = await JobProposal.Query(p => p.Status == (int)RequestStatus.EnAttente);
            ShowHome(player, seekers, pendingActs.Count, waiting.Count);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowHome(Player player, List<JobSeeker> seekers, int pendingActs, int waitingProposals)
        {
            Panel panel = ctx.PanelHelper.Create($"France Travail - {player.biz.BizName}", UIPanel.PanelType.Tab, player, () => Open(player));
            Live.Track("Agence.Accueil", panel);

            int pendingRegs = seekers.Count(s => s.Status == (int)RequestStatus.EnAttente);
            int validated = seekers.Count(s => s.Status == (int)RequestStatus.Validee);

            panel.AddTabLine(Counter("Inscriptions à traiter", pendingRegs), _ => { Live.Leave(panel); OpenRegistrations(player); });
            panel.AddTabLine(Counter("Actualisations à traiter", pendingActs), _ => { Live.Leave(panel); OpenPending(player); });
            panel.AddTabLine($"Demandeurs d'emploi ({validated})", _ => { Live.Leave(panel); OpenSeekers(player); });
            panel.AddTabLine($"Offres envoyées ({waitingProposals} sans réponse)", _ => { Live.Leave(panel); OpenProposals(player); });
            panel.AddTabLine("Historique des actualisations", _ => { Live.Leave(panel); OpenHistory(player); });

            panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        private static string Counter(string label, int count) =>
            count > 0 ? Color($"{label} ({count})", Colors.Warning) : $"{label} (0)";

        // ---------------------------- Inscriptions ----------------------------

        public async void OpenRegistrations(Player player)
        {
            if (!await CheckAccess(player)) return;
            List<JobSeeker> pending = (await ActiveSeekers()).Where(s => s.Status == (int)RequestStatus.EnAttente).OrderBy(s => s.CreatedAt).ToList();
            ShowRegistrations(player, pending);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowRegistrations(Player player, List<JobSeeker> pending)
        {
            Panel panel = ctx.PanelHelper.Create("Inscriptions à traiter", UIPanel.PanelType.Tab, player, () => OpenRegistrations(player));
            Live.Track("Agence.Inscriptions", panel);

            if (pending.Count == 0)
                panel.AddTabLine("Aucune inscription en attente", _ => { });
            foreach (JobSeeker s in pending)
                panel.AddTabLine($"{FtTime.Format(s.CreatedAt)} - {s.PlayerName} - {s.Sector}", _ => { Live.Leave(panel); OpenSeeker(player, s); });

            panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> ProcessRegistration(Player player, JobSeeker s, bool validated)
        {
            JobSeeker current = await JobSeeker.Query(s.Id);
            if (current == null || !current.Active || current.Status != (int)RequestStatus.EnAttente)
            {
                FranceTravailPlugin.Notify(player, "Cette inscription a déjà été traitée.", NotificationManager.Type.Warning);
                return true;
            }

            s = current; // version à jour (le versement du RSA a pu la modifier)
            s.Status = (int)(validated ? RequestStatus.Validee : RequestStatus.Refusee);
            s.ProcessedBy = player.FullName;
            s.ProcessedAt = FtTime.Now();
            if (!await s.Save())
            {
                FranceTravailPlugin.Notify(player, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error);
                return false;
            }

            FranceTravailPlugin.Notify(player, $"Inscription de {s.PlayerName} {(validated ? "validée" : "refusée")}.", NotificationManager.Type.Success);
            FranceTravailPlugin.Notify(FranceTravailPlugin.FindOnlinePlayer(s.CharacterId),
                validated ? "Votre inscription à France Travail est validée !" : "Votre inscription à France Travail a été refusée.",
                validated ? NotificationManager.Type.Success : NotificationManager.Type.Warning, 8f);
            Live.RefreshAll();
            return true;
        }

        // ---------------------------- Actualisations ----------------------------

        public async void OpenPending(Player player)
        {
            if (!await CheckAccess(player)) return;
            List<Actualisation> pending = await Actualisation.Query(a => a.Status == (int)RequestStatus.EnAttente);
            ShowPending(player, pending.OrderBy(a => a.CreatedAt).ToList());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowPending(Player player, List<Actualisation> pending)
        {
            Panel panel = ctx.PanelHelper.Create("Actualisations à traiter", UIPanel.PanelType.Tab, player, () => OpenPending(player));
            Live.Track("Agence.Actualisations", panel);

            if (pending.Count == 0)
                panel.AddTabLine("Aucune actualisation en attente", _ => { });
            foreach (Actualisation a in pending)
                panel.AddTabLine($"{FtTime.Format(a.CreatedAt)} - {a.PlayerName}", _ => { Live.Leave(panel); ShowActualisation(player, a); });

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
            Panel panel = ctx.PanelHelper.Create("Historique (100 dernières)", UIPanel.PanelType.Tab, player, () => OpenHistory(player));
            Live.Track("Agence.Historique", panel);

            if (list.Count == 0)
                panel.AddTabLine("Aucune actualisation", _ => { });
            foreach (Actualisation a in list)
                panel.AddTabLine($"{FtTime.Format(a.CreatedAt)} - {a.PlayerName} - {Labels.Status(a.Status)}", _ => { Live.Leave(panel); ShowActualisation(player, a); });

            panel.NextButton("Détails", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowActualisation(Player player, Actualisation a)
        {
            Panel panel = ctx.PanelHelper.Create(a.PlayerName, UIPanel.PanelType.Tab, player, () => ShowActualisation(player, a));
            panel.AddTabLine(Labels.Line("Envoyée le", FtTime.Format(a.CreatedAt)), _ => { });
            panel.AddTabLine(Labels.Line("Statut", Labels.Status(a.Status)), _ => { });
            if (!string.IsNullOrEmpty(a.ProcessedBy))
                panel.AddTabLine(Labels.Line("Traitée par", a.ProcessedBy), _ => { });
            Ui.InfoLines(panel, a.Summary());

            if (a.Status == (int)RequestStatus.EnAttente)
            {
                panel.PreviousButtonWithAction(Color("Valider", Colors.Success), async () => await ProcessActualisation(player, a, true));
                panel.PreviousButtonWithAction(Color("Refuser", Colors.Error), async () => await ProcessActualisation(player, a, false));
            }
            panel.AddButton("Dossier", _ => OpenSeekerByCharacter(player, a.CharacterId));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> ProcessActualisation(Player player, Actualisation a, bool validated)
        {
            Actualisation current = await Actualisation.Query(a.Id);
            if (current != null && current.Status != (int)RequestStatus.EnAttente)
            {
                FranceTravailPlugin.Notify(player, $"Déjà traitée par {current.ProcessedBy}.", NotificationManager.Type.Warning);
                return true;
            }

            a.Status = (int)(validated ? RequestStatus.Validee : RequestStatus.Refusee);
            a.ProcessedBy = player.FullName;
            a.ProcessedAt = FtTime.Now();
            if (!await a.Save())
            {
                FranceTravailPlugin.Notify(player, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error);
                return false;
            }

            FranceTravailPlugin.Notify(player, $"Actualisation de {a.PlayerName} {(validated ? "validée" : "refusée")}.", NotificationManager.Type.Success);
            FranceTravailPlugin.Notify(FranceTravailPlugin.FindOnlinePlayer(a.CharacterId),
                validated ? "Votre actualisation a été validée." : "Votre actualisation a été refusée. Contactez votre conseiller.",
                validated ? NotificationManager.Type.Success : NotificationManager.Type.Warning, 8f);
            Live.RefreshAll();
            return true;
        }

        // ---------------------------- Demandeurs ----------------------------

        public async void OpenSeekers(Player player)
        {
            if (!await CheckAccess(player)) return;
            List<JobSeeker> seekers = (await ActiveSeekers()).Where(s => s.Status == (int)RequestStatus.Validee).OrderBy(s => s.PlayerName).ToList();
            ShowSeekers(player, seekers);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowSeekers(Player player, List<JobSeeker> seekers)
        {
            Panel panel = ctx.PanelHelper.Create("Demandeurs d'emploi", UIPanel.PanelType.Tab, player, () => OpenSeekers(player));
            Live.Track("Agence.Demandeurs", panel);

            if (seekers.Count == 0)
                panel.AddTabLine("Aucun demandeur d'emploi", _ => { });
            foreach (JobSeeker s in seekers)
            {
                string available = s.Available ? Color("dispo", Colors.Success) : Color("pas dispo", Colors.Grey);
                panel.AddTabLine($"{s.PlayerName} - {s.Sector} - {available}", _ => { Live.Leave(panel); OpenSeeker(player, s); });
            }

            panel.NextButton("Dossier", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async void OpenSeekerByCharacter(Player player, int characterId)
        {
            JobSeeker seeker = await PlayerMenu.GetActiveSeeker(characterId);
            if (seeker == null)
            {
                FranceTravailPlugin.Notify(player, "Cette personne n'est plus inscrite.", NotificationManager.Type.Info);
                return;
            }
            OpenSeeker(player, seeker);
        }

        private async void OpenSeeker(Player player, JobSeeker seeker)
        {
            int characterId = seeker.CharacterId;
            List<Actualisation> history = await Actualisation.Query(a => a.CharacterId == characterId);
            List<JobProposal> proposals = await JobProposal.Query(p => p.CharacterId == characterId);
            ShowSeeker(player, seeker, history.OrderByDescending(a => a.CreatedAt).ToList(), proposals);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowSeeker(Player player, JobSeeker s, List<Actualisation> history, List<JobProposal> proposals)
        {
            Actualisation last = history.FirstOrDefault();
            Panel panel = ctx.PanelHelper.Create($"Dossier : {s.PlayerName}", UIPanel.PanelType.Tab, player, () => OpenSeeker(player, s));
            bool online = FranceTravailPlugin.FindOnlinePlayer(s.CharacterId) != null;

            panel.AddTabLine(Labels.Line("Inscription", Labels.Status(s.Status)), _ => { });
            panel.AddTabLine(Labels.Line("Inscrit le", FtTime.Format(s.CreatedAt)), _ => { });
            panel.AddTabLine(Labels.Line("Connecté", online ? Color("Oui", Colors.Success) : Color("Non", Colors.Grey)), _ => { });
            Ui.InfoLines(panel, s.Summary());
            panel.AddTabLine(Labels.Line("Actualisations", last == null ? "aucune" : $"{history.Count}, dernière le {FtTime.Format(last.CreatedAt)}"), _ => { });
            panel.AddTabLine(Labels.Line("Offres reçues", $"{proposals.Count} (acceptées : {proposals.Count(p => p.Status == (int)RequestStatus.Validee)})"), _ => { });
            panel.AddTabLine(ctx.Rsa.StateLine(s, history), _ => { });
            panel.AddTabLine(Labels.Line("RSA versé", $"{s.TotalRsa:0.##}€{(s.LastRsaAt > 0 ? $", dernier le {FtTime.Format(s.LastRsaAt)}" : "")}"), _ => { });

            if (s.Status == (int)RequestStatus.EnAttente)
            {
                panel.PreviousButtonWithAction(Color("Valider", Colors.Success), async () => await ProcessRegistration(player, s, true));
                panel.PreviousButtonWithAction(Color("Refuser", Colors.Error), async () => await ProcessRegistration(player, s, false));
            }
            else if (s.Status == (int)RequestStatus.Validee)
            {
                panel.AddButton("Proposer", _ => AskJobTitle(player, NewProposal(player, s)));
                panel.PreviousButtonWithAction(Color("Radier", Colors.Error), async () =>
                {
                    await ctx.PlayerMenu.EndRegistration(s, "Radiation");
                    FranceTravailPlugin.Notify(FranceTravailPlugin.FindOnlinePlayer(s.CharacterId),
                        "Vous avez été radié de France Travail.", NotificationManager.Type.Warning, 8f);
                    return true;
                });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---------------------------- Proposer un emploi ----------------------------

        private static JobProposal NewProposal(Player agent, JobSeeker seeker)
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
            Panel panel = ctx.PanelHelper.Create("Offre : poste", UIPanel.PanelType.Input, player, () => AskJobTitle(player, p));
            panel.TextLines.Add($"Quel poste proposer à {p.PlayerName} ?");
            panel.SetInputPlaceholder("Ex : Livreur, Vendeur...");
            panel.AddButton("Suivant", _ =>
            {
                string title = Ui.Clean(panel.inputText, 60);
                if (title == null)
                {
                    FranceTravailPlugin.Notify(player, "L'intitulé du poste est obligatoire.", NotificationManager.Type.Error);
                    return;
                }
                p.JobTitle = title;
                AskEmployer(player, p);
            });
            panel.PreviousButton();
            panel.CloseButton("Annuler");
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskEmployer(Player player, JobProposal p)
        {
            Panel panel = ctx.PanelHelper.Create("Offre : entreprise", UIPanel.PanelType.Tab, player, () => AskEmployer(player, p));

            panel.AddTabLine(Color("Autre (saisir le nom)", Colors.Info), _ => AskEmployerName(player, p));
            foreach (Bizs biz in Nova.biz.bizs.OrderBy(b => b.BizName))
            {
                string name = BizUtils.BizNameWithoutId(biz.BizName);
                panel.AddTabLine(name, _ => { p.Employer = name; AskDetails(player, p); });
            }

            panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton("Annuler");
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskEmployerName(Player player, JobProposal p)
        {
            Panel panel = ctx.PanelHelper.Create("Offre : entreprise", UIPanel.PanelType.Input, player, () => AskEmployerName(player, p));
            panel.TextLines.Add("Nom de l'entreprise qui recrute");
            panel.SetInputPlaceholder("Nom de l'entreprise");
            panel.AddButton("Suivant", _ =>
            {
                string employer = Ui.Clean(panel.inputText, 60);
                if (employer == null)
                {
                    FranceTravailPlugin.Notify(player, "Le nom de l'entreprise est obligatoire.", NotificationManager.Type.Error);
                    return;
                }
                p.Employer = employer;
                AskDetails(player, p);
            });
            panel.PreviousButton();
            panel.CloseButton("Annuler");
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskDetails(Player player, JobProposal p)
        {
            Panel panel = ctx.PanelHelper.Create("Offre : détails", UIPanel.PanelType.Input, player, () => AskDetails(player, p));
            Ui.OptionalInput(panel, "Salaire, horaires, contact... (facultatif)", "Ex : 1500€, contacter M. Dupont", 200,
                v => { p.Details = v; ConfirmProposal(player, p); });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ConfirmProposal(Player player, JobProposal p)
        {
            Panel panel = ctx.PanelHelper.Create($"Offre pour {p.PlayerName}", UIPanel.PanelType.Tab, player, () => ConfirmProposal(player, p));
            panel.AddTabLine(Labels.Line("Poste", p.JobTitle), _ => { });
            panel.AddTabLine(Labels.Line("Entreprise", p.Employer), _ => { });
            if (!string.IsNullOrEmpty(p.Details))
                panel.AddTabLine(Labels.Line("Détails", p.Details), _ => { });

            panel.CloseButtonWithAction(Color("Envoyer", Colors.Success), async () => await SendProposal(player, p));
            panel.PreviousButton("Modifier");
            panel.CloseButton("Annuler");
            panel.Display();
        }

        private async Task<bool> SendProposal(Player player, JobProposal p)
        {
            p.CreatedAt = FtTime.Now();
            p.Status = (int)RequestStatus.EnAttente;
            if (!await p.Save())
            {
                FranceTravailPlugin.Notify(player, "Erreur lors de l'envoi.", NotificationManager.Type.Error);
                return false;
            }

            FranceTravailPlugin.Notify(player, $"Offre envoyée à {p.PlayerName}.", NotificationManager.Type.Success);
            FranceTravailPlugin.Notify(FranceTravailPlugin.FindOnlinePlayer(p.CharacterId),
                $"Nouvelle offre d'emploi : {p.JobTitle} chez {p.Employer}. Passez à France Travail !", NotificationManager.Type.Info, 10f);
            Live.RefreshAll();
            return true;
        }

        // ---------------------------- Offres envoyées ----------------------------

        public async void OpenProposals(Player player)
        {
            if (!await CheckAccess(player)) return;
            List<JobProposal> proposals = await JobProposal.QueryAll();
            ShowProposals(player, proposals.OrderByDescending(p => p.CreatedAt).Take(100).ToList());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowProposals(Player player, List<JobProposal> proposals)
        {
            Panel panel = ctx.PanelHelper.Create("Offres envoyées", UIPanel.PanelType.Tab, player, () => OpenProposals(player));
            Live.Track("Agence.Offres", panel);

            if (proposals.Count == 0)
                panel.AddTabLine("Aucune offre envoyée", _ => { });
            foreach (JobProposal p in proposals)
                panel.AddTabLine($"{p.PlayerName} - {p.JobTitle} - {p.StatusLabel()}", _ => { Live.Leave(panel); ShowProposal(player, p); });

            panel.NextButton("Détails", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowProposal(Player player, JobProposal p)
        {
            Panel panel = ctx.PanelHelper.Create($"Offre pour {p.PlayerName}", UIPanel.PanelType.Tab, player, () => ShowProposal(player, p));
            Ui.InfoLines(panel, p.Summary());
            if (p.AnsweredAt > 0)
                panel.AddTabLine(Labels.Line("Réponse le", FtTime.Format(p.AnsweredAt)), _ => { });

            panel.PreviousButtonWithAction(Color("Supprimer", Colors.Error), async () =>
            {
                if (await p.Delete())
                {
                    Live.RefreshAll();
                    return true;
                }
                FranceTravailPlugin.Notify(player, "Erreur lors de la suppression.", NotificationManager.Type.Error);
                return false;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }

    // ======================================================================
    //  Points bleus (placés par le staff via AAMenu > Administration > Points bleus)
    // ======================================================================

    public enum PointKind { EspaceAgence = 0, Accueil = 1 }

    /// <summary>
    /// Modèle de point bleu France Travail. Deux types :
    /// - Espace agence : ouvre l'espace des employés de l'agence choisie (les autres sont refusés) ;
    /// - Accueil : ouvre l'espace demandeur d'emploi à n'importe quel joueur.
    /// Un modèle peut être placé autant de fois que voulu.
    /// </summary>
    public class FranceTravailPoint : ModEntity<FranceTravailPoint>, PatternData
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PatternName { get; set; }
        public int Kind { get; set; }
        /// <summary>Entreprise de l'agence (points « Espace agence »).</summary>
        public int BizId { get; set; }

        [Ignore]
        public string TypeName { get; set; } = nameof(FranceTravailPoint);

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        public FranceTravailPoint() { }

        public FranceTravailPoint(bool isCreated)
        {
            TypeName = nameof(FranceTravailPoint);
        }

        private static FranceTravailPlugin Plugin => FranceTravailPlugin.Instance;

        private string KindLabel() => Kind == (int)PointKind.Accueil ? "Accueil demandeurs" : "Espace agence";

        // ---------------------------- Joueur ----------------------------

        public void OnPlayerTrigger(Player player)
        {
            if (Kind == (int)PointKind.Accueil)
            {
                Plugin.PlayerMenu.Open(player);
                return;
            }

            if (!player.HasBiz || player.biz.Id != BizId)
            {
                FranceTravailPlugin.Notify(player, "Réservé aux employés de France Travail.", NotificationManager.Type.Error);
                return;
            }
            Plugin.AgencyMenu.Open(player);
        }

        // ---------------------------- Staff ----------------------------

        public async Task SetProperties(int id)
        {
            FranceTravailPoint result = await Query(id);
            Id = id;
            TypeName = nameof(FranceTravailPoint);
            PatternName = result?.PatternName;
            Kind = result?.Kind ?? 0;
            BizId = result?.BizId ?? 0;
        }

        private ModKit.ModKit Ctx => Context ?? Plugin;

        /// <summary>Menu principal staff : choisir un modèle et le placer à sa position.</summary>
        public async void CreateOrGenerate(Player player)
        {
            if (!player.IsAdmin) return;
            List<FranceTravailPoint> patterns = await QueryAll();
            ShowPatterns(player, patterns);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowPatterns(Player player, List<FranceTravailPoint> patterns)
        {
            Panel panel = Ctx.PanelHelper.Create("France Travail - Points", UIPanel.PanelType.Tab, player, () => CreateOrGenerate(player));

            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle, créez-en un", _ => { });
            foreach (FranceTravailPoint pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName} ({pattern.KindLabel()})", async _ =>
                {
                    pattern.TypeName = nameof(FranceTravailPoint);
                    pattern.Context = Ctx;
                    if (await Ctx.PointHelper.CreateNPoint(player, pattern))
                        FranceTravailPlugin.Notify(player, $"Point « {pattern.PatternName} » placé à votre position.", NotificationManager.Type.Success);
                    else
                        FranceTravailPlugin.Notify(player, "Erreur lors de la création du point.", NotificationManager.Type.Error);
                });
            }

            if (patterns.Count > 0)
                panel.AddButton("Placer ici", _ => panel.SelectTab());
            panel.NextButton("Nouveau", () => SetPatternData(player));
            panel.NextButton("Modèles", async () => await GetPatternData(player, true));
            panel.NextButton("Points", async () => await GetNPoints(player));
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Création d'un modèle : type de point, puis agence, puis nom.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void SetPatternData(Player player)
        {
            Panel panel = Ctx.PanelHelper.Create("Nouveau point : type", UIPanel.PanelType.Tab, player, () => SetPatternData(player));
            panel.AddTabLine("Espace agence (employés)", _ => AskBiz(player, new FranceTravailPoint(false) { Kind = (int)PointKind.EspaceAgence }));
            panel.AddTabLine("Accueil demandeurs (tous les joueurs)", _ => AskName(player, new FranceTravailPoint(false) { Kind = (int)PointKind.Accueil }));
            panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskBiz(Player player, FranceTravailPoint draft)
        {
            Panel panel = Ctx.PanelHelper.Create("Nouveau point : agence", UIPanel.PanelType.Tab, player, () => AskBiz(player, draft));
            foreach (Bizs biz in Nova.biz.bizs.OrderBy(b => b.BizName))
            {
                int bizId = biz.Id;
                panel.AddTabLine($"[{bizId}] {BizUtils.BizNameWithoutId(biz.BizName)}", _ => { draft.BizId = bizId; AskName(player, draft); });
            }
            panel.AddButton("Choisir", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AskName(Player player, FranceTravailPoint draft)
        {
            Panel panel = Ctx.PanelHelper.Create("Nouveau point : nom", UIPanel.PanelType.Input, player, () => AskName(player, draft));
            panel.TextLines.Add("Nom du point (ex : Agence de Saint-Branch)");
            panel.SetInputPlaceholder("France Travail");

            panel.CloseButtonWithAction("Créer", async () =>
            {
                draft.PatternName = Ui.Clean(panel.inputText, 60) ?? "France Travail";
                if (draft.Kind == (int)PointKind.EspaceAgence && !await Plugin.LinkAgency(draft.BizId))
                {
                    FranceTravailPlugin.Notify(player, "Impossible de déclarer cette entreprise comme agence.", NotificationManager.Type.Error);
                    return false;
                }
                if (!await draft.Save())
                {
                    FranceTravailPlugin.Notify(player, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error);
                    return false;
                }
                FranceTravailPlugin.Notify(player, $"Modèle « {draft.PatternName} » créé. Rouvrez le menu, choisissez-le puis « Placer ici ».", NotificationManager.Type.Success, 8f);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Liste des modèles : supprimer (avec tous ses points).</summary>
        public async Task GetPatternData(Player player, bool forEdit)
        {
            List<FranceTravailPoint> patterns = await QueryAll();
            ShowPatternList(player, patterns, forEdit);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowPatternList(Player player, List<FranceTravailPoint> patterns, bool forEdit)
        {
            Panel panel = Ctx.PanelHelper.Create("France Travail - Modèles", UIPanel.PanelType.Tab, player, async () => await GetPatternData(player, forEdit));

            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle", _ => { });
            foreach (FranceTravailPoint pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName} ({pattern.KindLabel()})", async _ =>
                {
                    if (!forEdit) return;
                    pattern.TypeName = nameof(FranceTravailPoint);
                    pattern.Context = Ctx;
                    await Ctx.PointHelper.DeleteNPointsByPattern(player, pattern);
                    if (await pattern.Delete())
                        FranceTravailPlugin.Notify(player, $"Modèle « {pattern.PatternName} » et ses points supprimés.", NotificationManager.Type.Success);
                    else
                        FranceTravailPlugin.Notify(player, "Erreur lors de la suppression.", NotificationManager.Type.Error);
                    panel.Refresh();
                });
            }

            if (patterns.Count > 0 && forEdit)
                panel.AddButton(Color("Supprimer", Colors.Error), _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Liste des points placés : se téléporter, déplacer ou supprimer.</summary>
        public async Task GetNPoints(Player player)
        {
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == nameof(FranceTravailPoint));
            Dictionary<int, string> names = (await QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);
            ShowNPoints(player, points, names);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ShowNPoints(Player player, List<NPoint> points, Dictionary<int, string> names)
        {
            string action = "";
            Panel panel = Ctx.PanelHelper.Create("France Travail - Points placés", UIPanel.PanelType.Tab, player, async () => await GetNPoints(player));

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
                            Ctx.PointHelper.PlayerSetPositionToNPoint(player, point);
                            break;
                        case "move":
                            if (await Ctx.PointHelper.SetNPointPosition(player, point))
                                FranceTravailPlugin.Notify(player, "Point déplacé à votre position.", NotificationManager.Type.Success);
                            break;
                        case "delete":
                            await Ctx.PointHelper.DeleteNPoint(point);
                            FranceTravailPlugin.Notify(player, $"Point #{point.Id} supprimé.", NotificationManager.Type.Success);
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
