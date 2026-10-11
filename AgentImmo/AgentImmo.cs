using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.AreaSystem;
using Life.DB;
using Life.Network;
using Life.PermissionSystem;
using Life.UI;
using Mirror;
using ModKit.Helper;
using ModKit.Helper.JobHelper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.ORM;
using ModKit.Utils;
using Newtonsoft.Json;
using SQLite;
using Logger = ModKit.Internal.Logger;
using static ModKit.Helper.TextFormattingHelper;

namespace AgentImmo
{
    /// <summary>
    /// Agent Immo by Loris Strange : métier d'agent immobilier.
    /// Les employés d'une agence (société liée à l'activité « Agent Immobilier ») vendent et louent
    /// les terrains depuis AAMenu &gt; Métier. Les joueurs consultent les annonces et gèrent leurs
    /// locations depuis AAMenu &gt; Interactions. Le staff configure tout depuis AAMenu &gt; Administration.
    /// </summary>
    public class AgentImmoPlugin : ModKit.ModKit
    {
        public const string ActivityName = "Agent Immobilier";
        public const string Title = "Agence immobilière";

        public static AgentImmoPlugin Instance;

        public ImmoConfig Config = new ImmoConfig();

        private string _configPath;
        private int _activityId;
        private bool _tickRunning;
        private readonly HashSet<int> _busyListings = new HashSet<int>();
        private readonly Dictionary<uint, uint> _lastAreaByNetId = new Dictionary<uint, uint>();

        public AgentImmoPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "Loris Strange");
            Instance = this;
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            Orm.RegisterTable<ImmoListing>();
            Orm.RegisterTable<ImmoLease>();
            Orm.RegisterTable<ImmoLog>();
            Orm.RegisterTable<ImmoPayout>();
            Orm.RegisterTable<ImmoRequest>();

            LoadConfig();
            InitActivity();
            RegisterMenus();

            Nova.server.OnMinutePassedEvent += OnMinutePassed;

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", "initialisé (by Loris Strange)");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            DeliverPayouts(player, character.Id);
        }

        public override void OnPlayerEnterArea(Player player, AreaBox area)
        {
            base.OnPlayerEnterArea(player, area);
            if (player != null && area != null)
                _lastAreaByNetId[player.netId] = area.areaId;
        }

        public override void OnPlayerDisconnect(NetworkConnection conn)
        {
            base.OnPlayerDisconnect(conn);
            Player player = Nova.server.Players.FirstOrDefault(p => p != null && p.conn == conn);
            if (player != null)
                _lastAreaByNetId.Remove(player.netId);
        }

        // ------------------------------------------------------------------
        //  Initialisation
        // ------------------------------------------------------------------

        private void LoadConfig()
        {
            try
            {
                string directory = Path.Combine(PluginPath, "AgentImmo");
                Directory.CreateDirectory(directory);
                _configPath = Path.Combine(directory, "config.json");

                if (File.Exists(_configPath))
                    Config = JsonConvert.DeserializeObject<ImmoConfig>(File.ReadAllText(_configPath)) ?? new ImmoConfig();
                SaveConfig();
            }
            catch (Exception ex)
            {
                Logger.LogError(PluginInformations.SourceName, "Lecture de la configuration impossible : " + ex.Message);
            }
        }

        public void SaveConfig()
        {
            try
            {
                File.WriteAllText(_configPath, JsonConvert.SerializeObject(Config, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Logger.LogError(PluginInformations.SourceName, "Sauvegarde de la configuration impossible : " + ex.Message);
            }
        }

        private async void InitActivity()
        {
            try
            {
                await JobHelper.AddCustomActivity(ActivityName);
                await GetActivityId();
            }
            catch (Exception ex)
            {
                Logger.LogError(PluginInformations.SourceName, "Création de l'activité impossible : " + ex.Message);
            }
        }

        /// <summary>Id de l'activité personnalisée « Agent Immobilier » (créée si besoin).</summary>
        public async Task<int> GetActivityId()
        {
            if (_activityId > 0) return _activityId;

            List<CustomActivity> activities = await ModEntity<CustomActivity>.Query(a => a.Name == ActivityName);
            if (activities.Count == 0)
            {
                await JobHelper.AddCustomActivity(ActivityName);
                activities = await ModEntity<CustomActivity>.Query(a => a.Name == ActivityName);
            }
            _activityId = activities.Count > 0 ? activities[0].Id : 0;
            return _activityId;
        }

        private void RegisterMenus()
        {
            if (AAMenu.AAMenu.menu == null)
            {
                Logger.LogError(PluginInformations.SourceName, "AAMenu introuvable : le plugin AAMenu doit être installé.");
                return;
            }

            // AAMenu > Métier : visible uniquement pour les sociétés liées à l'activité « Agent Immobilier »
            AAMenu.Menu.AddBizTabLine(PluginInformations, null, new CustomActivity { Name = ActivityName }, Title,
                ui => AgencyMenu.Root(PanelHelper.ReturnPlayerFromPanel(ui)));

            // AAMenu > Interactions : côté joueurs / clients
            AAMenu.Menu.AddInteractionTabLine(PluginInformations, Title,
                ui => ClientMenu.Root(PanelHelper.ReturnPlayerFromPanel(ui)));

            // AAMenu > Administration
            AAMenu.Menu.AddAdminTabLine(PluginInformations, 1, "Agent Immo",
                ui => AdminMenu.Root(PanelHelper.ReturnPlayerFromPanel(ui)));
            AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, 1, "Agent Immo",
                ui => AdminMenu.Root(PanelHelper.ReturnPlayerFromPanel(ui)));
        }

        // ------------------------------------------------------------------
        //  Agences
        // ------------------------------------------------------------------

        public async Task<bool> IsAgency(int bizId)
        {
            if (bizId <= 0) return false;
            int activityId = await GetActivityId();
            List<CustomBiz> links = await ModEntity<CustomBiz>.Query(c => c.BizId == bizId && c.CustomActivityId == activityId);
            return links.Count > 0;
        }

        /// <summary>Retourne la société du joueur si c'est une agence immobilière, sinon null (avec notification).</summary>
        public async Task<Bizs> GetAgentAgency(Player player)
        {
            if (player == null || !player.HasBiz || !await IsAgency(player.character.BizId))
            {
                player?.Notify(Title, "Vous ne travaillez pas dans une agence immobilière.", NotificationManager.Type.Error);
                return null;
            }
            if (Config.RequireService && !player.serviceMetier)
            {
                player.Notify(Title, "Vous devez être en service pour exercer.", NotificationManager.Type.Warning);
                return null;
            }
            return player.biz;
        }

        public static bool IsBoss(Player player, Bizs biz)
        {
            return biz != null && player?.character != null && biz.OwnerId == player.character.Id;
        }

        public static string BizName(int bizId)
        {
            Bizs biz = Nova.biz.FetchBiz(bizId);
            return biz != null ? BizUtils.BizNameWithoutId(biz.BizName ?? "") : $"Agence #{bizId}";
        }

        public static List<Player> OnlineEmployees(int bizId)
        {
            return Nova.server.Players.Where(p => p?.character != null && p.character.BizId == bizId).ToList();
        }

        // ------------------------------------------------------------------
        //  Terrains
        // ------------------------------------------------------------------

        public static LifeArea GetArea(int areaId)
        {
            return areaId > 0 ? Nova.a.GetAreaById((uint)areaId) : null;
        }

        public static bool HasOwner(LifeArea area)
        {
            return area?.permissions != null && area.permissions.HasOwner();
        }

        public static int OwnerId(LifeArea area)
        {
            return area?.permissions?.owner?.characterId ?? 0;
        }

        public static bool IsCoOwner(LifeArea area, int characterId)
        {
            return area?.permissions?.coOwners != null && area.permissions.coOwners.Any(e => e.characterId == characterId);
        }

        public static string AreaLabel(int areaId)
        {
            LifeArea area = GetArea(areaId);
            string address = area?.address;
            return string.IsNullOrWhiteSpace(address) ? $"Terrain n°{areaId}" : $"Terrain n°{areaId} - {address}";
        }

        /// <summary>Terrain dans lequel se trouve le joueur (0 si aucun).</summary>
        public uint CurrentAreaId(Player player)
        {
            if (player?.setup != null && player.setup.areaId > 0)
                return player.setup.areaId;
            return _lastAreaByNetId.TryGetValue(player.netId, out uint id) ? id : 0;
        }

        /// <summary>Donne la pleine propriété du terrain (vente ou location d'un terrain de l'État).</summary>
        public static void SetOwner(LifeArea area, int characterId)
        {
            if (area.permissions == null) area.permissions = new Permissions();
            area.permissions.owner = characterId > 0 ? new Entity { characterId = characterId } : new Entity();
            area.permissions.coOwners = new List<Entity>();
            area.rentHours = 0; // la location est gérée par l'agence, pas par le minuteur du jeu
            area.Save();
        }

        public static void AddCoOwner(LifeArea area, int characterId)
        {
            if (area.permissions == null) area.permissions = new Permissions();
            if (area.permissions.coOwners == null) area.permissions.coOwners = new List<Entity>();
            if (!IsCoOwner(area, characterId))
                area.AddCoOwner(new Entity { characterId = characterId });
            area.Save();
        }

        public static void RemoveCoOwner(LifeArea area, int characterId)
        {
            if (IsCoOwner(area, characterId))
                area.DeleteCoOwner(new Entity { characterId = characterId });
            area.Save();
        }

        public static void SetGps(Player player, int areaId)
        {
            try
            {
                player.setup.TargetSetGPSTarget(Nova.a.GetAreaPosition((uint)areaId));
                player.Notify(Title, $"GPS : {AreaLabel(areaId)}", NotificationManager.Type.Info);
            }
            catch (Exception ex)
            {
                Logger.LogError(Instance.PluginInformations.SourceName, "GPS : " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        //  Joueurs / argent
        // ------------------------------------------------------------------

        public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static string Money(double amount) => $"{Math.Round(amount, 2):N2}€";

        public static string Date(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("dd/MM/yyyy HH:mm");

        public static Player FindOnline(int characterId)
        {
            if (characterId <= 0) return null;
            return Nova.server.Players.FirstOrDefault(p => p?.character != null && p.character.Id == characterId);
        }

        /// <summary>Client le plus proche de l'agent (dans la distance configurée).</summary>
        public Player GetNearbyClient(Player agent)
        {
            Player client = agent.GetClosestPlayer();
            if (client?.character == null || client == agent || client.character.Id == agent.character.Id)
                return null;
            float distance = UnityEngine.Vector3.Distance(agent.setup.transform.position, client.setup.transform.position);
            return distance <= Config.MaxClientDistance ? client : null;
        }

        /// <summary>Verse de l'argent sur le compte d'un personnage (en attente s'il est hors ligne).</summary>
        public static async Task PayCharacter(int characterId, double amount, string reason)
        {
            amount = Math.Round(amount, 2);
            if (characterId <= 0 || amount <= 0) return;

            Player player = FindOnline(characterId);
            if (player != null)
            {
                player.AddBankMoney(amount, reason);
                player.Notify(Title, $"{reason} : +{Money(amount)} sur votre compte.", NotificationManager.Type.Success);
                return;
            }
            await new ImmoPayout { CharacterId = characterId, Amount = amount, Reason = reason, CreatedAt = Now }.Save();
        }

        public static void PayAgency(int bizId, double amount, string reason)
        {
            amount = Math.Round(amount, 2);
            if (amount <= 0) return;
            Nova.biz.FetchBiz(bizId)?.AddBankMoney(amount, reason);
        }

        /// <summary>
        /// Répartit un paiement : propriétaire (ou l'État), agence et prime de l'agent.
        /// Retourne la part totale de l'agence (prime de l'agent incluse).
        /// </summary>
        public async Task<double> Distribute(int bizId, int ownerId, int agentId, double amount, string reason)
        {
            double agencyShare = ownerId > 0
                ? amount * Config.CommissionPercent / 100.0
                : amount * Config.StateAgencyPercent / 100.0;
            agencyShare = Math.Round(Math.Max(0, Math.Min(amount, agencyShare)), 2);

            double agentBonus = agentId > 0 ? Math.Round(agencyShare * Config.AgentBonusPercent / 100.0, 2) : 0;

            if (ownerId > 0)
                await PayCharacter(ownerId, amount - agencyShare, reason);
            PayAgency(bizId, agencyShare - agentBonus, reason);
            if (agentBonus > 0)
                await PayCharacter(agentId, agentBonus, "Prime agent immobilier");

            return agencyShare;
        }

        private async void DeliverPayouts(Player player, int characterId)
        {
            try
            {
                List<ImmoPayout> payouts = await ImmoPayout.Query(p => p.CharacterId == characterId);
                if (payouts.Count == 0) return;

                double total = 0;
                foreach (ImmoPayout payout in payouts)
                {
                    if (!await payout.Delete()) continue;
                    player.AddBankMoney(payout.Amount, payout.Reason);
                    total += payout.Amount;
                }
                if (total > 0)
                    player.Notify(Title, $"Paiements immobiliers reçus pendant votre absence : +{Money(total)}", NotificationManager.Type.Success, 10f);
            }
            catch (Exception ex)
            {
                Logger.LogError(PluginInformations.SourceName, "Versement en attente : " + ex.Message);
            }
        }

        public static async Task Log(int bizId, int areaId, string type, double amount, double agencyShare, string client, string agent)
        {
            await new ImmoLog
            {
                BizId = bizId,
                AreaId = areaId,
                Type = type,
                Amount = Math.Round(amount, 2),
                AgencyShare = Math.Round(agencyShare, 2),
                ClientName = client,
                AgentName = agent,
                CreatedAt = Now
            }.Save();
        }

        // ------------------------------------------------------------------
        //  Annonces
        // ------------------------------------------------------------------

        /// <summary>Vérifie qu'un terrain peut être mis en annonce (pas d'annonce ni de location en cours).</summary>
        public static async Task<string> CheckAreaAvailable(int areaId)
        {
            if (GetArea(areaId) == null) return "Ce terrain n'existe pas.";
            if ((await ImmoListing.Query(l => l.AreaId == areaId && l.Status == ImmoListing.Active)).Count > 0)
                return "Ce terrain a déjà une annonce active.";
            if ((await ImmoLease.Query(l => l.AreaId == areaId && l.Active == true)).Count > 0)
                return "Ce terrain est actuellement loué via une agence.";
            return null;
        }

        /// <summary>Vérifie que l'annonce est toujours valable (propriétaire inchangé, terrain libre...).</summary>
        public static string CheckListingStillValid(ImmoListing listing, LifeArea area)
        {
            if (listing == null || listing.Status != ImmoListing.Active) return "Cette annonce n'est plus disponible.";
            if (area == null) return "Ce terrain n'existe plus.";
            if (listing.OwnerCharacterId == 0 && HasOwner(area)) return "Ce terrain de l'État a déjà été acquis.";
            if (listing.OwnerCharacterId > 0 && OwnerId(area) != listing.OwnerCharacterId) return "Le propriétaire de ce terrain a changé.";
            return null;
        }

        // ------------------------------------------------------------------
        //  Vente / location
        // ------------------------------------------------------------------

        public async Task<bool> ConcludeSale(Player agent, Player client, int listingId)
        {
            if (!_busyListings.Add(listingId)) return false;
            try
            {
                ImmoListing listing = await ImmoListing.Query(listingId);
                LifeArea area = GetArea(listing?.AreaId ?? 0);
                string error = CheckListingStillValid(listing, area);
                if (error == null && listing.Kind != ImmoListing.Sale) error = "Cette annonce n'est pas une vente.";
                if (error == null && client.character.Id == listing.OwnerCharacterId) error = "Le client est déjà propriétaire de ce terrain.";
                if (error == null && client.Bank < listing.Price) error = $"Fonds insuffisants ({Money(listing.Price)} requis).";
                if (error != null)
                {
                    client.Notify(Title, error, NotificationManager.Type.Error);
                    agent?.Notify(Title, error, NotificationManager.Type.Error);
                    return false;
                }

                client.AddBankMoney(-listing.Price, $"Achat {AreaLabel(listing.AreaId)}");
                SetOwner(area, client.character.Id);

                listing.Status = ImmoListing.Done;
                await listing.Save();

                // Une éventuelle location agence du terrain n'a plus lieu d'être
                foreach (ImmoLease lease in await ImmoLease.Query(l => l.AreaId == listing.AreaId && l.Active == true))
                    await EndLease(lease, "Terrain vendu", false);

                double agencyShare = await Distribute(listing.BizId, listing.OwnerCharacterId, agent?.character?.Id ?? 0,
                    listing.Price, $"Vente {AreaLabel(listing.AreaId)}");
                await Log(listing.BizId, listing.AreaId, "Vente", listing.Price, agencyShare, client.FullName, agent?.FullName ?? "-");

                client.Notify(Title, $"Félicitations ! Vous êtes propriétaire du {AreaLabel(listing.AreaId)}.", NotificationManager.Type.Success, 10f);
                agent?.Notify(Title, $"Vente conclue avec {client.FullName} : {Money(listing.Price)} (agence : {Money(agencyShare)}).", NotificationManager.Type.Success, 10f);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(PluginInformations.SourceName, "Vente : " + ex);
                return false;
            }
            finally
            {
                _busyListings.Remove(listingId);
            }
        }

        public async Task<bool> ConcludeRent(Player agent, Player client, int listingId)
        {
            if (!_busyListings.Add(listingId)) return false;
            try
            {
                ImmoListing listing = await ImmoListing.Query(listingId);
                LifeArea area = GetArea(listing?.AreaId ?? 0);
                string error = CheckListingStillValid(listing, area);
                if (error == null && listing.Kind != ImmoListing.Rent) error = "Cette annonce n'est pas une location.";
                if (error == null && client.character.Id == listing.OwnerCharacterId) error = "Le client est le propriétaire de ce terrain.";
                if (error == null && client.Bank < listing.Price) error = $"Fonds insuffisants (premier loyer : {Money(listing.Price)}).";
                if (error == null)
                {
                    int tenantId = client.character.Id;
                    int count = (await ImmoLease.Query(l => l.TenantCharacterId == tenantId && l.Active == true)).Count;
                    if (Config.MaxLeasesPerPlayer > 0 && count >= Config.MaxLeasesPerPlayer)
                        error = $"Le client a déjà {count} location(s) (maximum {Config.MaxLeasesPerPlayer}).";
                }
                if (error != null)
                {
                    client.Notify(Title, error, NotificationManager.Type.Error);
                    agent?.Notify(Title, error, NotificationManager.Type.Error);
                    return false;
                }

                client.AddBankMoney(-listing.Price, $"Loyer {AreaLabel(listing.AreaId)}");

                // Terrain de l'État : le locataire en devient l'occupant (propriétaire temporaire).
                // Terrain d'un joueur : le propriétaire le reste, le locataire devient co-propriétaire.
                if (listing.OwnerCharacterId == 0)
                    SetOwner(area, client.character.Id);
                else
                    AddCoOwner(area, client.character.Id);

                bool granted = listing.OwnerCharacterId == 0
                    ? OwnerId(area) == client.character.Id
                    : IsCoOwner(area, client.character.Id);
                if (!granted)
                {
                    client.AddBankMoney(listing.Price, "Remboursement loyer");
                    client.Notify(Title, "Impossible de vous donner l'accès à ce terrain : loyer remboursé.", NotificationManager.Type.Error);
                    agent?.Notify(Title, "Le jeu a refusé l'accès du locataire (trop de co-propriétaires ?).", NotificationManager.Type.Error);
                    return false;
                }

                int periodDays = Math.Max(1, listing.PeriodDays);
                ImmoLease lease = new ImmoLease
                {
                    ListingId = listing.Id,
                    BizId = listing.BizId,
                    AreaId = listing.AreaId,
                    TenantCharacterId = client.character.Id,
                    TenantName = client.FullName,
                    LandlordCharacterId = listing.OwnerCharacterId,
                    LandlordName = listing.OwnerName,
                    Rent = listing.Price,
                    PeriodDays = periodDays,
                    StartAt = Now,
                    NextDueAt = Now + periodDays * 86400L,
                    AgentName = agent?.FullName ?? "-",
                    Active = true
                };
                await lease.Save();

                listing.Status = ImmoListing.Done;
                await listing.Save();

                double agencyShare = await Distribute(listing.BizId, listing.OwnerCharacterId, agent?.character?.Id ?? 0,
                    listing.Price, $"Location {AreaLabel(listing.AreaId)}");
                await Log(listing.BizId, listing.AreaId, "Location (1er loyer)", listing.Price, agencyShare, client.FullName, agent?.FullName ?? "-");

                client.Notify(Title, $"Bail signé pour le {AreaLabel(listing.AreaId)}. Prochain loyer le {Date(lease.NextDueAt)}.", NotificationManager.Type.Success, 10f);
                agent?.Notify(Title, $"Location conclue avec {client.FullName} : {Money(listing.Price)} / {periodDays} j.", NotificationManager.Type.Success, 10f);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(PluginInformations.SourceName, "Location : " + ex);
                return false;
            }
            finally
            {
                _busyListings.Remove(listingId);
            }
        }

        /// <summary>Met fin à un bail : rend le terrain et remet l'annonce en ligne si demandé.</summary>
        public async Task EndLease(ImmoLease lease, string reason, bool relist = true)
        {
            if (lease == null || !lease.Active) return;

            LifeArea area = GetArea(lease.AreaId);
            if (area != null)
            {
                if (lease.LandlordCharacterId == 0)
                {
                    if (OwnerId(area) == lease.TenantCharacterId)
                        SetOwner(area, 0);
                }
                else
                {
                    RemoveCoOwner(area, lease.TenantCharacterId);
                }
            }

            lease.Active = false;
            lease.EndedAt = Now;
            lease.EndReason = reason;
            await lease.Save();

            if (relist)
            {
                ImmoListing listing = await ImmoListing.Query(lease.ListingId);
                if (listing != null && listing.Status == ImmoListing.Done && listing.Kind == ImmoListing.Rent
                    && CheckListingStillValid(new ImmoListing { Status = ImmoListing.Active, OwnerCharacterId = listing.OwnerCharacterId }, area) == null
                    && await CheckAreaAvailable(listing.AreaId) == null)
                {
                    listing.Status = ImmoListing.Active;
                    await listing.Save();
                }
            }

            await Log(lease.BizId, lease.AreaId, "Fin de bail : " + reason, 0, 0, lease.TenantName, "-");

            FindOnline(lease.TenantCharacterId)?.Notify(Title, $"Votre bail du {AreaLabel(lease.AreaId)} est terminé ({reason}).", NotificationManager.Type.Warning, 10f);
            FindOnline(lease.LandlordCharacterId)?.Notify(Title, $"Le bail de {lease.TenantName} sur votre {AreaLabel(lease.AreaId)} est terminé ({reason}).", NotificationManager.Type.Info, 10f);
        }

        /// <summary>Encaisse un loyer (prélèvement ou paiement d'avance).</summary>
        public async Task<bool> PayRent(ImmoLease lease, Player tenant, string type)
        {
            if (tenant.Bank < lease.Rent) return false;

            tenant.AddBankMoney(-lease.Rent, $"Loyer {AreaLabel(lease.AreaId)}");
            lease.NextDueAt += Math.Max(1, lease.PeriodDays) * 86400L;
            lease.LastReminderAt = 0;
            await lease.Save();

            double agencyShare = await Distribute(lease.BizId, lease.LandlordCharacterId, 0, lease.Rent, $"Loyer {AreaLabel(lease.AreaId)}");
            await Log(lease.BizId, lease.AreaId, type, lease.Rent, agencyShare, lease.TenantName, "-");

            tenant.Notify(Title, $"Loyer payé : {Money(lease.Rent)}. Prochaine échéance le {Date(lease.NextDueAt)}.", NotificationManager.Type.Success, 8f);
            return true;
        }

        // ------------------------------------------------------------------
        //  Prélèvement automatique des loyers
        // ------------------------------------------------------------------

        private async void OnMinutePassed()
        {
            if (_tickRunning) return;
            _tickRunning = true;
            try
            {
                foreach (ImmoLease lease in await ImmoLease.Query(l => l.Active == true))
                {
                    LifeArea area = GetArea(lease.AreaId);

                    // Le terrain a changé de main en dehors de l'agence (abandon, vente directe, staff...)
                    if (area == null)
                    {
                        await EndLease(lease, "Terrain supprimé", false);
                        continue;
                    }
                    if (lease.LandlordCharacterId == 0 && OwnerId(area) != lease.TenantCharacterId)
                    {
                        await EndLease(lease, "Terrain quitté", true);
                        continue;
                    }
                    if (lease.LandlordCharacterId > 0 && (OwnerId(area) != lease.LandlordCharacterId || !IsCoOwner(area, lease.TenantCharacterId)))
                    {
                        await EndLease(lease, "Terrain quitté ou vendu", false);
                        continue;
                    }

                    long now = Now;
                    if (now < lease.NextDueAt) continue;

                    Player tenant = FindOnline(lease.TenantCharacterId);
                    if (tenant != null && await PayRent(lease, tenant, "Loyer (prélèvement)"))
                        continue;

                    if (now >= lease.NextDueAt + Config.RentGraceHours * 3600L)
                    {
                        await EndLease(lease, "Loyer impayé - expulsion", true);
                        continue;
                    }

                    // Rappel au locataire (au plus une fois par heure)
                    if (tenant != null && now - lease.LastReminderAt >= 3600)
                    {
                        lease.LastReminderAt = now;
                        await lease.Save();
                        long left = lease.NextDueAt + Config.RentGraceHours * 3600L - now;
                        tenant.Notify(Title, $"Loyer impayé ({Money(lease.Rent)}) pour le {AreaLabel(lease.AreaId)}. " +
                            $"Approvisionnez votre compte : expulsion dans {Math.Max(1, left / 3600)} h.", NotificationManager.Type.Warning, 10f);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(PluginInformations.SourceName, "Loyers : " + ex);
            }
            finally
            {
                _tickRunning = false;
            }
        }
    }

    // ======================================================================
    //  Configuration
    // ======================================================================

    public class ImmoConfig
    {
        /// <summary>Commission de l'agence sur les biens des joueurs (ventes et loyers), en %.</summary>
        public double CommissionPercent { get; set; } = 10;

        /// <summary>Part gardée par l'agence sur les terrains de l'État (le reste va à l'État), en %.</summary>
        public double StateAgencyPercent { get; set; } = 25;

        /// <summary>Part de la commission versée directement à l'agent qui conclut, en %.</summary>
        public double AgentBonusPercent { get; set; } = 20;

        /// <summary>Délai (heures) après l'échéance avant expulsion d'un locataire qui n'a pas payé.</summary>
        public int RentGraceHours { get; set; } = 48;

        /// <summary>Durée par défaut d'une période de location (jours).</summary>
        public int DefaultRentPeriodDays { get; set; } = 7;

        /// <summary>Nombre maximum de locations agence par joueur (0 = illimité).</summary>
        public int MaxLeasesPerPlayer { get; set; } = 3;

        /// <summary>Distance maximale (mètres) entre l'agent et le client.</summary>
        public float MaxClientDistance { get; set; } = 5f;

        /// <summary>Autoriser les agences à vendre / louer les terrains libres de l'État.</summary>
        public bool AllowStateTerrains { get; set; } = true;

        /// <summary>L'agent doit être en service (métier) pour utiliser le menu de l'agence.</summary>
        public bool RequireService { get; set; } = false;
    }

    // ======================================================================
    //  Base de données (ModKit ORM)
    // ======================================================================

    /// <summary>Annonce d'une agence : vente ou location d'un terrain.</summary>
    public class ImmoListing : ModEntity<ImmoListing>
    {
        public const int Sale = 0;
        public const int Rent = 1;

        public const int Active = 0;
        public const int Done = 1;
        public const int Removed = 2;

        [AutoIncrement][PrimaryKey] public int Id { get; set; }
        public int BizId { get; set; }
        public int AreaId { get; set; }
        public int Kind { get; set; }
        public double Price { get; set; }
        public int PeriodDays { get; set; }
        /// <summary>0 = terrain de l'État.</summary>
        public int OwnerCharacterId { get; set; }
        public string OwnerName { get; set; }
        public string Description { get; set; }
        public string CreatedBy { get; set; }
        public long CreatedAt { get; set; }
        public int Status { get; set; }

        [Ignore] public bool IsSale => Kind == Sale;
        [Ignore] public string KindLabel => Kind == Sale ? "Vente" : "Location";
        [Ignore] public string PriceLabel => Kind == Sale ? AgentImmoPlugin.Money(Price) : $"{AgentImmoPlugin.Money(Price)} / {PeriodDays} j";
        [Ignore] public string OwnerLabel => OwnerCharacterId == 0 ? "État" : (OwnerName ?? $"#{OwnerCharacterId}");
        [Ignore] public string StatusLabel => Status == Active ? "En ligne" : Status == Done ? (Kind == Sale ? "Vendu" : "Loué") : "Retiré";
    }

    /// <summary>Bail de location géré par une agence.</summary>
    public class ImmoLease : ModEntity<ImmoLease>
    {
        [AutoIncrement][PrimaryKey] public int Id { get; set; }
        public int ListingId { get; set; }
        public int BizId { get; set; }
        public int AreaId { get; set; }
        public int TenantCharacterId { get; set; }
        public string TenantName { get; set; }
        /// <summary>0 = terrain de l'État.</summary>
        public int LandlordCharacterId { get; set; }
        public string LandlordName { get; set; }
        public double Rent { get; set; }
        public int PeriodDays { get; set; }
        public long StartAt { get; set; }
        public long NextDueAt { get; set; }
        public long LastReminderAt { get; set; }
        public string AgentName { get; set; }
        public bool Active { get; set; }
        public long EndedAt { get; set; }
        public string EndReason { get; set; }
    }

    /// <summary>Historique des opérations d'une agence.</summary>
    public class ImmoLog : ModEntity<ImmoLog>
    {
        [AutoIncrement][PrimaryKey] public int Id { get; set; }
        public int BizId { get; set; }
        public int AreaId { get; set; }
        public string Type { get; set; }
        public double Amount { get; set; }
        public double AgencyShare { get; set; }
        public string ClientName { get; set; }
        public string AgentName { get; set; }
        public long CreatedAt { get; set; }
    }

    /// <summary>Argent dû à un joueur hors ligne, versé à sa prochaine connexion.</summary>
    public class ImmoPayout : ModEntity<ImmoPayout>
    {
        [AutoIncrement][PrimaryKey] public int Id { get; set; }
        public int CharacterId { get; set; }
        public double Amount { get; set; }
        public string Reason { get; set; }
        public long CreatedAt { get; set; }
    }

    /// <summary>Demande de contact d'un client pour une annonce.</summary>
    public class ImmoRequest : ModEntity<ImmoRequest>
    {
        [AutoIncrement][PrimaryKey] public int Id { get; set; }
        public int BizId { get; set; }
        public int ListingId { get; set; }
        public int AreaId { get; set; }
        public int CharacterId { get; set; }
        public string CharacterName { get; set; }
        public string Message { get; set; }
        public long CreatedAt { get; set; }
        public bool Handled { get; set; }
    }

    /// <summary>Brouillon d'annonce pendant sa création dans les menus.</summary>
    public class ListingDraft
    {
        public int AreaId;
        public int OwnerCharacterId;
        public string OwnerName;
        public int Kind;
        public double Price;
        public int PeriodDays;
        /// <summary>Propriétaire présent à qui proposer le mandat (null pour un terrain de l'État).</summary>
        public Player Owner;
    }

    // ======================================================================
    //  AAMenu > Métier : menu de l'agence
    // ======================================================================

    public static class AgencyMenu
    {
        private static AgentImmoPlugin P => AgentImmoPlugin.Instance;
        private const string T = AgentImmoPlugin.Title;

        public static async void Root(Player player)
        {
            if (player == null) return;
            Bizs biz = await P.GetAgentAgency(player);
            if (biz == null) return;

            int bizId = biz.Id;
            int listings = (await ImmoListing.Query(l => l.BizId == bizId && l.Status == ImmoListing.Active)).Count;
            int leases = (await ImmoLease.Query(l => l.BizId == bizId && l.Active == true)).Count;
            int requests = (await ImmoRequest.Query(r => r.BizId == bizId && r.Handled == false)).Count;

            Panel panel = P.PanelHelper.Create($"{AgentImmoPlugin.BizName(bizId)} - Agence", UIPanel.PanelType.Tab, player, () => Root(player));
            panel.AddTabLine($"Catalogue des biens ({listings})", _ => Catalogue(player, bizId));
            panel.AddTabLine("Nouveau bien : terrain de l'État", _ => NewStateListing(player, bizId));
            panel.AddTabLine("Nouveau mandat : terrain d'un client", _ => NewMandate(player, bizId));
            panel.AddTabLine($"Locations en cours ({leases})", _ => Leases(player, bizId));
            panel.AddTabLine(requests > 0 ? Color($"Demandes clients ({requests})", Colors.Warning) : "Demandes clients (0)", _ => Requests(player, bizId));
            panel.AddTabLine("Terrain où je me trouve", _ => AreaInfo(player, (int)P.CurrentAreaId(player)));
            panel.AddTabLine("Historique des opérations", _ => History(player, bizId));
            panel.AddTabLine("Comptes de l'agence", _ => Accounts(player, bizId));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.AddButton("Retour", _ => { panel.Close(); AAMenu.AAMenu.menu.BizPanel(player); });
            panel.CloseButton();
            panel.Display();
        }

        // ---------------- Catalogue ----------------

        private static async void Catalogue(Player player, int bizId)
        {
            List<ImmoListing> listings = (await ImmoListing.Query(l => l.BizId == bizId && l.Status == ImmoListing.Active))
                .OrderBy(l => l.AreaId).ToList();

            Panel panel = P.PanelHelper.Create("Agence - Catalogue", UIPanel.PanelType.TabPrice, player, () => Catalogue(player, bizId));
            if (listings.Count == 0)
                panel.AddTabLine("Aucun bien en catalogue", _ => { });
            foreach (ImmoListing listing in listings)
            {
                panel.AddTabLine($"[{listing.KindLabel}] {AgentImmoPlugin.AreaLabel(listing.AreaId)}<br>{Size(Color("Propriétaire : " + listing.OwnerLabel, Colors.Grey), 14)}",
                    listing.PriceLabel, IconUtils.Others.None.Id, _ => ListingDetail(player, bizId, listing.Id));
            }
            if (listings.Count > 0)
                panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async void ListingDetail(Player player, int bizId, int listingId)
        {
            ImmoListing listing = await ImmoListing.Query(listingId);
            if (listing == null || listing.BizId != bizId)
            {
                player.Notify(T, "Annonce introuvable.", NotificationManager.Type.Error);
                return;
            }
            LifeArea area = AgentImmoPlugin.GetArea(listing.AreaId);
            string validity = AgentImmoPlugin.CheckListingStillValid(listing, area);

            Panel panel = P.PanelHelper.Create($"Annonce n°{listing.Id}", UIPanel.PanelType.Tab, player, () => ListingDetail(player, bizId, listingId));
            panel.TextLines.Add(Bold(AgentImmoPlugin.AreaLabel(listing.AreaId)));
            panel.TextLines.Add($"Type : {listing.KindLabel} - Prix : {Color(listing.PriceLabel, Colors.Success)}");
            panel.TextLines.Add($"Propriétaire : {listing.OwnerLabel} - Statut : {listing.StatusLabel}");
            if (!string.IsNullOrWhiteSpace(listing.Description))
                panel.TextLines.Add(Italic(listing.Description));
            if (validity != null)
                panel.TextLines.Add(Color(validity, Colors.Error));

            panel.AddTabLine(listing.IsSale ? "Proposer la vente au client proche" : "Proposer la location au client proche", _ => ProposeDeal(player, listing.Id));
            panel.AddTabLine("GPS vers le terrain", _ => AgentImmoPlugin.SetGps(player, listing.AreaId));
            panel.AddTabLine("Envoyer le GPS au client proche", _ =>
            {
                Player client = P.GetNearbyClient(player);
                if (client == null)
                {
                    player.Notify(T, "Aucun client à proximité.", NotificationManager.Type.Warning);
                    return;
                }
                AgentImmoPlugin.SetGps(client, listing.AreaId);
                player.Notify(T, $"GPS envoyé à {client.FullName}.", NotificationManager.Type.Success);
            });
            panel.AddTabLine("Modifier le prix", _ => EditPrice(player, listing));
            if (!listing.IsSale)
                panel.AddTabLine("Modifier la durée de location", _ => EditPeriod(player, listing));
            panel.AddTabLine("Modifier la description", _ => EditDescription(player, listing));
            panel.AddTabLine(Color("Retirer l'annonce", Colors.Error), async _ =>
            {
                listing.Status = ImmoListing.Removed;
                await listing.Save();
                await AgentImmoPlugin.Log(bizId, listing.AreaId, "Annonce retirée", 0, 0, listing.OwnerLabel, player.FullName);
                AgentImmoPlugin.FindOnline(listing.OwnerCharacterId)?.Notify(T, $"L'agence a retiré l'annonce de votre {AgentImmoPlugin.AreaLabel(listing.AreaId)}.", NotificationManager.Type.Info);
                player.Notify(T, "Annonce retirée.", NotificationManager.Type.Success);
                panel.Previous();
            });

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void EditPrice(Player player, ImmoListing listing)
        {
            Panel panel = P.PanelHelper.Create($"Annonce n°{listing.Id} - Prix", UIPanel.PanelType.Input, player, () => EditPrice(player, listing));
            panel.TextLines.Add($"Prix actuel : {listing.PriceLabel}");
            panel.TextLines.Add(listing.IsSale ? "Nouveau prix de vente :" : "Nouveau loyer par période :");
            panel.SetInputPlaceholder(listing.Price.ToString());
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                if (!TryParseMoney(panel.inputText, out double price))
                {
                    player.Notify(T, "Montant invalide.", NotificationManager.Type.Error);
                    return false;
                }
                listing.Price = price;
                await listing.Save();
                player.Notify(T, $"Nouveau prix : {listing.PriceLabel}", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void EditPeriod(Player player, ImmoListing listing)
        {
            Panel panel = P.PanelHelper.Create($"Annonce n°{listing.Id} - Durée", UIPanel.PanelType.Input, player, () => EditPeriod(player, listing));
            panel.TextLines.Add($"Durée actuelle d'une période : {listing.PeriodDays} jour(s)");
            panel.TextLines.Add("Nouvelle durée (jours) :");
            panel.SetInputPlaceholder(listing.PeriodDays.ToString());
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                if (!int.TryParse(panel.inputText, out int days) || days < 1 || days > 365)
                {
                    player.Notify(T, "Durée invalide (1 à 365 jours).", NotificationManager.Type.Error);
                    return false;
                }
                listing.PeriodDays = days;
                await listing.Save();
                player.Notify(T, $"Nouvelle durée : {days} jour(s).", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void EditDescription(Player player, ImmoListing listing)
        {
            Panel panel = P.PanelHelper.Create($"Annonce n°{listing.Id} - Description", UIPanel.PanelType.Input, player, () => EditDescription(player, listing));
            panel.TextLines.Add("Description affichée aux clients (surface, quartier, atouts...) :");
            panel.SetInputPlaceholder(listing.Description ?? "Belle maison avec jardin...");
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string text = panel.inputText?.Trim() ?? "";
                listing.Description = text.Length > 200 ? text.Substring(0, 200) : text;
                await listing.Save();
                player.Notify(T, "Description mise à jour.", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---------------- Proposition au client ----------------

        private static async void ProposeDeal(Player agent, int listingId)
        {
            ImmoListing listing = await ImmoListing.Query(listingId);
            string error = AgentImmoPlugin.CheckListingStillValid(listing, AgentImmoPlugin.GetArea(listing?.AreaId ?? 0));
            if (error != null)
            {
                agent.Notify(T, error, NotificationManager.Type.Error);
                return;
            }

            Player client = P.GetNearbyClient(agent);
            if (client == null)
            {
                agent.Notify(T, "Aucun client à proximité. Rapprochez-vous de lui.", NotificationManager.Type.Warning);
                return;
            }
            if (client.character.Id == listing.OwnerCharacterId)
            {
                agent.Notify(T, "Ce joueur est le propriétaire du terrain.", NotificationManager.Type.Warning);
                return;
            }

            string agency = AgentImmoPlugin.BizName(listing.BizId);
            Panel panel = P.PanelHelper.Create($"{agency} - Proposition", UIPanel.PanelType.Text, client, () => ProposeDeal(agent, listingId));
            panel.TextLines.Add($"{Bold(agent.FullName)} de l'agence {Bold(agency)} vous propose :");
            panel.TextLines.Add(Bold(AgentImmoPlugin.AreaLabel(listing.AreaId)));
            if (listing.IsSale)
            {
                panel.TextLines.Add($"Achat au prix de {Color(AgentImmoPlugin.Money(listing.Price), Colors.Success)}");
            }
            else
            {
                panel.TextLines.Add($"Location : {Color(AgentImmoPlugin.Money(listing.Price), Colors.Success)} tous les {listing.PeriodDays} jour(s)");
                panel.TextLines.Add($"Premier loyer payé aujourd'hui, puis prélevé automatiquement. Expulsion après {P.Config.RentGraceHours} h d'impayé.");
            }
            panel.TextLines.Add($"Propriétaire : {listing.OwnerLabel}");
            if (!string.IsNullOrWhiteSpace(listing.Description))
                panel.TextLines.Add(Italic(listing.Description));
            panel.TextLines.Add(Color($"Votre solde bancaire : {AgentImmoPlugin.Money(client.Bank)}", Colors.Grey));

            panel.CloseButtonWithAction(listing.IsSale ? "Acheter" : "Signer le bail", async () =>
                listing.IsSale
                    ? await P.ConcludeSale(agent, client, listingId)
                    : await P.ConcludeRent(agent, client, listingId));
            panel.AddButton("Visiter (GPS)", _ => AgentImmoPlugin.SetGps(client, listing.AreaId));
            panel.AddButton("Refuser", _ =>
            {
                panel.Close();
                agent.Notify(T, $"{client.FullName} a refusé votre proposition.", NotificationManager.Type.Warning);
            });
            panel.Display();

            agent.Notify(T, $"Proposition envoyée à {client.FullName}.", NotificationManager.Type.Info);
        }

        // ---------------- Création d'annonces ----------------

        private static void NewStateListing(Player player, int bizId)
        {
            if (!P.Config.AllowStateTerrains)
            {
                player.Notify(T, "La vente des terrains de l'État est désactivée par l'administration.", NotificationManager.Type.Warning);
                return;
            }

            uint currentArea = P.CurrentAreaId(player);
            Panel panel = P.PanelHelper.Create("Agence - Terrain de l'État", UIPanel.PanelType.Input, player, () => NewStateListing(player, bizId));
            panel.TextLines.Add("Numéro du terrain libre à mettre en vente ou en location :");
            if (currentArea > 0)
                panel.TextLines.Add(Color($"Vous êtes sur le terrain n°{currentArea}.", Colors.Info));
            panel.SetInputPlaceholder(currentArea > 0 ? currentArea.ToString() : "Numéro du terrain");

            panel.NextButton("Continuer", async () =>
            {
                string input = string.IsNullOrWhiteSpace(panel.inputText) && currentArea > 0 ? currentArea.ToString() : panel.inputText;
                if (!int.TryParse(input, out int areaId))
                {
                    player.Notify(T, "Numéro de terrain invalide.", NotificationManager.Type.Error);
                    return;
                }
                LifeArea area = AgentImmoPlugin.GetArea(areaId);
                string error = await AgentImmoPlugin.CheckAreaAvailable(areaId);
                if (error == null && AgentImmoPlugin.HasOwner(area))
                    error = "Ce terrain appartient déjà à quelqu'un : passez par un mandat.";
                if (error != null)
                {
                    player.Notify(T, error, NotificationManager.Type.Error);
                    return;
                }
                ChooseKind(player, bizId, new ListingDraft { AreaId = areaId });
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void NewMandate(Player agent, int bizId)
        {
            Player client = P.GetNearbyClient(agent);
            if (client == null)
            {
                agent.Notify(T, "Le propriétaire doit être à côté de vous pour signer un mandat.", NotificationManager.Type.Warning);
                return;
            }

            LifeTerrain[] terrains = Nova.a.GetOwnedTerrains(client.character.Id, true).terrains ?? new LifeTerrain[0];

            Panel panel = P.PanelHelper.Create($"Mandat - Terrains de {client.FullName}", UIPanel.PanelType.Tab, agent, () => NewMandate(agent, bizId));
            if (terrains.Length == 0)
                panel.AddTabLine($"{client.FullName} ne possède aucun terrain", _ => { });
            foreach (LifeTerrain terrain in terrains)
            {
                int areaId = (int)terrain.id;
                panel.AddTabLine(AgentImmoPlugin.AreaLabel(areaId), async _ =>
                {
                    string error = await AgentImmoPlugin.CheckAreaAvailable(areaId);
                    if (error == null && AgentImmoPlugin.OwnerId(AgentImmoPlugin.GetArea(areaId)) != client.character.Id)
                        error = "Ce client n'est pas le propriétaire principal de ce terrain.";
                    if (error != null)
                    {
                        agent.Notify(T, error, NotificationManager.Type.Error);
                        return;
                    }
                    ChooseKind(agent, bizId, new ListingDraft
                    {
                        AreaId = areaId,
                        OwnerCharacterId = client.character.Id,
                        OwnerName = client.FullName,
                        Owner = client
                    });
                });
            }
            if (terrains.Length > 0)
                panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void ChooseKind(Player player, int bizId, ListingDraft draft)
        {
            Panel panel = P.PanelHelper.Create($"{AgentImmoPlugin.AreaLabel(draft.AreaId)} - Type", UIPanel.PanelType.Tab, player, () => ChooseKind(player, bizId, draft));
            panel.AddTabLine("Mise en vente", _ => { draft.Kind = ImmoListing.Sale; AskPrice(player, bizId, draft); });
            panel.AddTabLine("Mise en location", _ => { draft.Kind = ImmoListing.Rent; AskPrice(player, bizId, draft); });
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void AskPrice(Player player, int bizId, ListingDraft draft)
        {
            LifeArea area = AgentImmoPlugin.GetArea(draft.AreaId);
            double suggested = draft.Kind == ImmoListing.Sale ? area?.price ?? 0 : area?.rentPrice ?? 0;

            Panel panel = P.PanelHelper.Create($"{AgentImmoPlugin.AreaLabel(draft.AreaId)} - Prix", UIPanel.PanelType.Input, player, () => AskPrice(player, bizId, draft));
            panel.TextLines.Add(draft.Kind == ImmoListing.Sale ? "Prix de vente :" : "Loyer par période :");
            if (suggested > 0)
                panel.TextLines.Add(Color($"Prix de référence du jeu : {AgentImmoPlugin.Money(suggested)}", Colors.Grey));
            panel.SetInputPlaceholder(suggested > 0 ? suggested.ToString() : "Montant en €");
            panel.NextButton("Continuer", () =>
            {
                string input = string.IsNullOrWhiteSpace(panel.inputText) && suggested > 0 ? suggested.ToString() : panel.inputText;
                if (!TryParseMoney(input, out double price))
                {
                    player.Notify(T, "Montant invalide.", NotificationManager.Type.Error);
                    return;
                }
                draft.Price = price;
                if (draft.Kind == ImmoListing.Rent)
                    AskPeriod(player, bizId, draft);
                else
                    Confirm(player, bizId, draft);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void AskPeriod(Player player, int bizId, ListingDraft draft)
        {
            int defaultDays = P.Config.DefaultRentPeriodDays;
            Panel panel = P.PanelHelper.Create($"{AgentImmoPlugin.AreaLabel(draft.AreaId)} - Durée", UIPanel.PanelType.Input, player, () => AskPeriod(player, bizId, draft));
            panel.TextLines.Add("Durée d'une période de location (jours) : le loyer est prélevé à chaque période.");
            panel.SetInputPlaceholder(defaultDays.ToString());
            panel.NextButton("Continuer", () =>
            {
                string input = string.IsNullOrWhiteSpace(panel.inputText) ? defaultDays.ToString() : panel.inputText;
                if (!int.TryParse(input, out int days) || days < 1 || days > 365)
                {
                    player.Notify(T, "Durée invalide (1 à 365 jours).", NotificationManager.Type.Error);
                    return;
                }
                draft.PeriodDays = days;
                Confirm(player, bizId, draft);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void Confirm(Player player, int bizId, ListingDraft draft)
        {
            bool isMandate = draft.OwnerCharacterId > 0;
            double commission = isMandate ? P.Config.CommissionPercent : P.Config.StateAgencyPercent;
            string price = draft.Kind == ImmoListing.Sale ? AgentImmoPlugin.Money(draft.Price) : $"{AgentImmoPlugin.Money(draft.Price)} / {draft.PeriodDays} j";

            Panel panel = P.PanelHelper.Create($"{AgentImmoPlugin.AreaLabel(draft.AreaId)} - Confirmation", UIPanel.PanelType.Text, player, () => Confirm(player, bizId, draft));
            panel.TextLines.Add(Bold(AgentImmoPlugin.AreaLabel(draft.AreaId)));
            panel.TextLines.Add($"{(draft.Kind == ImmoListing.Sale ? "Vente" : "Location")} : {Color(price, Colors.Success)}");
            panel.TextLines.Add($"Propriétaire : {(isMandate ? draft.OwnerName : "État")}");
            panel.TextLines.Add($"Part de l'agence : {commission}% (dont {P.Config.AgentBonusPercent}% de prime pour l'agent qui conclut)");
            if (isMandate)
                panel.TextLines.Add(Color($"Le mandat sera envoyé à {draft.OwnerName} pour signature.", Colors.Info));

            panel.CloseButtonWithAction(isMandate ? "Envoyer le mandat" : "Publier l'annonce", async () =>
            {
                if (isMandate)
                    return SendMandate(player, bizId, draft);
                return await CreateListing(player, bizId, draft);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static bool SendMandate(Player agent, int bizId, ListingDraft draft)
        {
            Player owner = draft.Owner;
            if (owner?.character == null || AgentImmoPlugin.FindOnline(draft.OwnerCharacterId) == null)
            {
                agent.Notify(T, "Le propriétaire n'est plus connecté.", NotificationManager.Type.Error);
                return false;
            }

            string agency = AgentImmoPlugin.BizName(bizId);
            double commission = P.Config.CommissionPercent;
            string price = draft.Kind == ImmoListing.Sale ? AgentImmoPlugin.Money(draft.Price) : $"{AgentImmoPlugin.Money(draft.Price)} / {draft.PeriodDays} j";

            Panel panel = P.PanelHelper.Create($"{agency} - Mandat", UIPanel.PanelType.Text, owner, () => SendMandate(agent, bizId, draft));
            panel.TextLines.Add($"{Bold(agent.FullName)} de l'agence {Bold(agency)} vous propose un mandat de {(draft.Kind == ImmoListing.Sale ? "vente" : "location")} :");
            panel.TextLines.Add(Bold(AgentImmoPlugin.AreaLabel(draft.AreaId)));
            panel.TextLines.Add($"Prix affiché : {Color(price, Colors.Success)}");
            panel.TextLines.Add($"Commission de l'agence : {commission}% - vous recevrez {AgentImmoPlugin.Money(draft.Price * (100 - commission) / 100.0)} par {(draft.Kind == ImmoListing.Sale ? "vente" : "loyer")}.");
            if (draft.Kind == ImmoListing.Rent)
                panel.TextLines.Add("Vous restez propriétaire : le locataire est ajouté en co-propriétaire pendant le bail.");
            panel.TextLines.Add(Color("Vous pourrez retirer l'annonce à tout moment depuis AAMenu > Interactions.", Colors.Grey));

            panel.CloseButtonWithAction("Signer le mandat", async () =>
            {
                draft.Owner = owner;
                if (AgentImmoPlugin.OwnerId(AgentImmoPlugin.GetArea(draft.AreaId)) != owner.character.Id)
                {
                    owner.Notify(T, "Vous n'êtes plus propriétaire de ce terrain.", NotificationManager.Type.Error);
                    return true;
                }
                bool ok = await CreateListing(agent, bizId, draft);
                if (ok) owner.Notify(T, "Mandat signé : votre bien est en ligne.", NotificationManager.Type.Success);
                return true;
            });
            panel.AddButton("Refuser", _ =>
            {
                panel.Close();
                agent.Notify(T, $"{owner.FullName} a refusé le mandat.", NotificationManager.Type.Warning);
            });
            panel.Display();

            agent.Notify(T, $"Mandat envoyé à {owner.FullName}.", NotificationManager.Type.Info);
            return true;
        }

        private static async Task<bool> CreateListing(Player agent, int bizId, ListingDraft draft)
        {
            string error = await AgentImmoPlugin.CheckAreaAvailable(draft.AreaId);
            LifeArea area = AgentImmoPlugin.GetArea(draft.AreaId);
            if (error == null && draft.OwnerCharacterId == 0 && AgentImmoPlugin.HasOwner(area))
                error = "Ce terrain a été acquis entre-temps.";
            if (error != null)
            {
                agent.Notify(T, error, NotificationManager.Type.Error);
                return false;
            }

            ImmoListing listing = new ImmoListing
            {
                BizId = bizId,
                AreaId = draft.AreaId,
                Kind = draft.Kind,
                Price = draft.Price,
                PeriodDays = draft.Kind == ImmoListing.Rent ? Math.Max(1, draft.PeriodDays) : 0,
                OwnerCharacterId = draft.OwnerCharacterId,
                OwnerName = draft.OwnerName,
                Description = "",
                CreatedBy = agent.FullName,
                CreatedAt = AgentImmoPlugin.Now,
                Status = ImmoListing.Active
            };
            if (!await listing.Save())
            {
                agent.Notify(T, "Erreur lors de l'enregistrement de l'annonce.", NotificationManager.Type.Error);
                return false;
            }
            await AgentImmoPlugin.Log(bizId, draft.AreaId, "Nouvelle annonce (" + listing.KindLabel + ")", listing.Price, 0, listing.OwnerLabel, agent.FullName);
            agent.Notify(T, $"Annonce publiée : {AgentImmoPlugin.AreaLabel(draft.AreaId)} ({listing.PriceLabel}).", NotificationManager.Type.Success);
            return true;
        }

        // ---------------- Locations ----------------

        private static async void Leases(Player player, int bizId)
        {
            List<ImmoLease> leases = (await ImmoLease.Query(l => l.BizId == bizId && l.Active == true)).OrderBy(l => l.NextDueAt).ToList();

            Panel panel = P.PanelHelper.Create("Agence - Locations en cours", UIPanel.PanelType.TabPrice, player, () => Leases(player, bizId));
            if (leases.Count == 0)
                panel.AddTabLine("Aucune location en cours", _ => { });
            foreach (ImmoLease lease in leases)
            {
                bool late = lease.NextDueAt <= AgentImmoPlugin.Now;
                string due = late ? Color("IMPAYÉ", Colors.Error) : "échéance " + AgentImmoPlugin.Date(lease.NextDueAt);
                panel.AddTabLine($"{AgentImmoPlugin.AreaLabel(lease.AreaId)}<br>{Size(Color($"{lease.TenantName} - {due}", Colors.Grey), 14)}",
                    $"{AgentImmoPlugin.Money(lease.Rent)} / {lease.PeriodDays} j", IconUtils.Others.None.Id, _ => LeaseDetail(player, bizId, lease.Id));
            }
            if (leases.Count > 0)
                panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async void LeaseDetail(Player player, int bizId, int leaseId)
        {
            ImmoLease lease = await ImmoLease.Query(leaseId);
            if (lease == null || lease.BizId != bizId || !lease.Active)
            {
                player.Notify(T, "Ce bail n'est plus actif.", NotificationManager.Type.Warning);
                return;
            }

            Panel panel = P.PanelHelper.Create($"Bail n°{lease.Id}", UIPanel.PanelType.Tab, player, () => LeaseDetail(player, bizId, leaseId));
            panel.TextLines.Add(Bold(AgentImmoPlugin.AreaLabel(lease.AreaId)));
            panel.TextLines.Add($"Locataire : {lease.TenantName} - Propriétaire : {(lease.LandlordCharacterId == 0 ? "État" : lease.LandlordName)}");
            panel.TextLines.Add($"Loyer : {AgentImmoPlugin.Money(lease.Rent)} / {lease.PeriodDays} j - depuis le {AgentImmoPlugin.Date(lease.StartAt)}");
            panel.TextLines.Add($"Prochaine échéance : {AgentImmoPlugin.Date(lease.NextDueAt)} (signé par {lease.AgentName})");

            panel.AddTabLine("GPS vers le terrain", _ => AgentImmoPlugin.SetGps(player, lease.AreaId));
            panel.AddTabLine(Color("Résilier le bail / expulser le locataire", Colors.Error), async _ =>
            {
                await P.EndLease(lease, $"résilié par l'agence ({player.FullName})", true);
                player.Notify(T, "Bail résilié.", NotificationManager.Type.Success);
                panel.Previous();
            });
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---------------- Demandes clients ----------------

        private static async void Requests(Player player, int bizId)
        {
            List<ImmoRequest> requests = (await ImmoRequest.Query(r => r.BizId == bizId && r.Handled == false)).OrderBy(r => r.CreatedAt).ToList();

            Panel panel = P.PanelHelper.Create("Agence - Demandes clients", UIPanel.PanelType.Tab, player, () => Requests(player, bizId));
            if (requests.Count == 0)
                panel.AddTabLine("Aucune demande en attente", _ => { });
            foreach (ImmoRequest request in requests)
            {
                bool online = AgentImmoPlugin.FindOnline(request.CharacterId) != null;
                panel.AddTabLine($"{request.CharacterName} {(online ? Color("(en ligne)", Colors.Success) : Color("(hors ligne)", Colors.Grey))}<br>" +
                    Size(Color($"{AgentImmoPlugin.AreaLabel(request.AreaId)} - {request.Message}", Colors.Grey), 14),
                    _ => RequestDetail(player, bizId, request));
            }
            if (requests.Count > 0)
                panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void RequestDetail(Player player, int bizId, ImmoRequest request)
        {
            Panel panel = P.PanelHelper.Create($"Demande n°{request.Id}", UIPanel.PanelType.Tab, player, () => RequestDetail(player, bizId, request));
            panel.TextLines.Add($"Client : {Bold(request.CharacterName)} - le {AgentImmoPlugin.Date(request.CreatedAt)}");
            panel.TextLines.Add($"Bien : {AgentImmoPlugin.AreaLabel(request.AreaId)}");
            panel.TextLines.Add(Italic(request.Message ?? ""));

            panel.AddTabLine("GPS vers le terrain", _ => AgentImmoPlugin.SetGps(player, request.AreaId));
            panel.AddTabLine("Prévenir le client que j'arrive", _ =>
            {
                Player client = AgentImmoPlugin.FindOnline(request.CharacterId);
                if (client == null)
                {
                    player.Notify(T, "Le client n'est pas connecté.", NotificationManager.Type.Warning);
                    return;
                }
                client.Notify(T, $"{player.FullName} ({AgentImmoPlugin.BizName(bizId)}) a pris en charge votre demande et vous rejoint au {AgentImmoPlugin.AreaLabel(request.AreaId)}.", NotificationManager.Type.Info, 10f);
                AgentImmoPlugin.SetGps(client, request.AreaId);
                player.Notify(T, "Client prévenu (GPS envoyé).", NotificationManager.Type.Success);
            });
            panel.AddTabLine(Color("Marquer comme traitée", Colors.Success), async _ =>
            {
                request.Handled = true;
                await request.Save();
                player.Notify(T, "Demande traitée.", NotificationManager.Type.Success);
                panel.Previous();
            });
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---------------- Infos ----------------

        public static void AreaInfo(Player player, int areaId)
        {
            LifeArea area = AgentImmoPlugin.GetArea(areaId);
            if (area == null)
            {
                player.Notify(T, "Vous n'êtes sur aucun terrain.", NotificationManager.Type.Warning);
                return;
            }

            Panel panel = P.PanelHelper.Create($"Terrain n°{areaId}", UIPanel.PanelType.Text, player, () => AreaInfo(player, areaId));
            panel.TextLines.Add(Bold(AgentImmoPlugin.AreaLabel(areaId)));
            panel.TextLines.Add($"Prix de référence : {AgentImmoPlugin.Money(area.price)} - Loyer de référence : {AgentImmoPlugin.Money(area.rentPrice)}");
            panel.TextLines.Add(AgentImmoPlugin.HasOwner(area)
                ? $"Occupé (propriétaire : personnage n°{AgentImmoPlugin.OwnerId(area)}, {area.permissions.coOwners?.Count ?? 0} co-propriétaire(s))"
                : Color("Libre (propriété de l'État)", Colors.Success));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async void History(Player player, int bizId)
        {
            List<ImmoLog> logs = (await ImmoLog.Query(l => l.BizId == bizId)).OrderByDescending(l => l.CreatedAt).Take(50).ToList();

            Panel panel = P.PanelHelper.Create("Agence - Historique", UIPanel.PanelType.TabPrice, player, () => History(player, bizId));
            if (logs.Count == 0)
                panel.AddTabLine("Aucune opération", _ => { });
            foreach (ImmoLog log in logs)
            {
                panel.AddTabLine($"{log.Type} - {AgentImmoPlugin.AreaLabel(log.AreaId)}<br>" +
                    Size(Color($"{AgentImmoPlugin.Date(log.CreatedAt)} - client : {log.ClientName} - agent : {log.AgentName}", Colors.Grey), 14),
                    log.Amount > 0 ? AgentImmoPlugin.Money(log.Amount) : "-", IconUtils.Others.None.Id, _ => { });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async void Accounts(Player player, int bizId)
        {
            Bizs biz = Nova.biz.FetchBiz(bizId);
            List<ImmoLog> logs = await ImmoLog.Query(l => l.BizId == bizId);
            long monthAgo = AgentImmoPlugin.Now - 30L * 86400;
            int listings = (await ImmoListing.Query(l => l.BizId == bizId && l.Status == ImmoListing.Active)).Count;
            int leases = (await ImmoLease.Query(l => l.BizId == bizId && l.Active == true)).Count;

            Panel panel = P.PanelHelper.Create("Agence - Comptes", UIPanel.PanelType.Text, player, () => Accounts(player, bizId));
            panel.TextLines.Add(Bold(AgentImmoPlugin.BizName(bizId)));
            if (AgentImmoPlugin.IsBoss(player, biz))
                panel.TextLines.Add($"Compte de la société : {Color(AgentImmoPlugin.Money(biz?.Bank ?? 0), Colors.Success)}");
            panel.TextLines.Add($"Biens en catalogue : {listings} - Locations en cours : {leases}");
            panel.TextLines.Add($"Ventes conclues : {logs.Count(l => l.Type == "Vente")} - Baux signés : {logs.Count(l => l.Type.StartsWith("Location"))}");
            panel.TextLines.Add($"Commissions (30 derniers jours) : {AgentImmoPlugin.Money(logs.Where(l => l.CreatedAt >= monthAgo).Sum(l => l.AgencyShare))}");
            panel.TextLines.Add($"Commissions (total) : {AgentImmoPlugin.Money(logs.Sum(l => l.AgencyShare))}");
            panel.TextLines.Add(Color($"Barème : {P.Config.CommissionPercent}% sur les biens des particuliers, {P.Config.StateAgencyPercent}% sur les terrains de l'État, prime agent {P.Config.AgentBonusPercent}% de la commission.", Colors.Grey));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public static bool TryParseMoney(string input, out double amount)
        {
            amount = 0;
            if (string.IsNullOrWhiteSpace(input)) return false;
            string clean = input.Replace("€", "").Replace(" ", "").Replace(",", ".");
            if (!double.TryParse(clean, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out amount))
                return false;
            amount = Math.Round(amount, 2);
            return amount > 0 && amount < 1_000_000_000;
        }
    }

    // ======================================================================
    //  AAMenu > Interactions : côté joueurs
    // ======================================================================

    public static class ClientMenu
    {
        private static AgentImmoPlugin P => AgentImmoPlugin.Instance;
        private const string T = AgentImmoPlugin.Title;

        public static async void Root(Player player)
        {
            if (player?.character == null) return;
            int characterId = player.character.Id;
            int leases = (await ImmoLease.Query(l => l.TenantCharacterId == characterId && l.Active == true)).Count;
            int mandates = (await ImmoListing.Query(l => l.OwnerCharacterId == characterId && l.Status == ImmoListing.Active)).Count;
            int rentedOut = (await ImmoLease.Query(l => l.LandlordCharacterId == characterId && l.Active == true)).Count;

            Panel panel = P.PanelHelper.Create(T, UIPanel.PanelType.Tab, player, () => Root(player));
            panel.AddTabLine("Annonces immobilières", _ => Ads(player, ImmoListing.Sale, "Ventes"));
            panel.AddTabLine("Annonces de location", _ => Ads(player, ImmoListing.Rent, "Locations"));
            panel.AddTabLine($"Mes locations ({leases})", _ => MyLeases(player));
            panel.AddTabLine($"Mes biens en agence ({mandates})", _ => MyMandates(player));
            panel.AddTabLine($"Mes biens loués ({rentedOut})", _ => MyRentedOut(player));
            panel.AddTabLine("Agences immobilières", _ => Agencies(player));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.AddButton("Retour", _ => { panel.Close(); AAMenu.AAMenu.menu.InteractionPanel(player); });
            panel.CloseButton();
            panel.Display();
        }

        private static async void Ads(Player player, int kind, string label)
        {
            List<ImmoListing> listings = (await ImmoListing.Query(l => l.Kind == kind && l.Status == ImmoListing.Active))
                .OrderBy(l => l.Price).ToList();

            Panel panel = P.PanelHelper.Create($"Annonces - {label}", UIPanel.PanelType.TabPrice, player, () => Ads(player, kind, label));
            if (listings.Count == 0)
                panel.AddTabLine("Aucune annonce pour le moment", _ => { });
            foreach (ImmoListing listing in listings)
            {
                panel.AddTabLine($"{AgentImmoPlugin.AreaLabel(listing.AreaId)}<br>{Size(Color(AgentImmoPlugin.BizName(listing.BizId), Colors.Grey), 14)}",
                    listing.PriceLabel, IconUtils.Others.None.Id, _ => AdDetail(player, listing.Id));
            }
            if (listings.Count > 0)
                panel.NextButton("Voir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async void AdDetail(Player player, int listingId)
        {
            ImmoListing listing = await ImmoListing.Query(listingId);
            if (listing == null || listing.Status != ImmoListing.Active)
            {
                player.Notify(T, "Cette annonce n'est plus disponible.", NotificationManager.Type.Warning);
                return;
            }

            string agency = AgentImmoPlugin.BizName(listing.BizId);
            int agents = AgentImmoPlugin.OnlineEmployees(listing.BizId).Count;

            Panel panel = P.PanelHelper.Create($"Annonce - {AgentImmoPlugin.AreaLabel(listing.AreaId)}", UIPanel.PanelType.Tab, player, () => AdDetail(player, listingId));
            panel.TextLines.Add(Bold(AgentImmoPlugin.AreaLabel(listing.AreaId)));
            panel.TextLines.Add($"{listing.KindLabel} : {Color(listing.PriceLabel, Colors.Success)}");
            panel.TextLines.Add($"Agence : {agency} ({agents} agent(s) en ligne)");
            if (!string.IsNullOrWhiteSpace(listing.Description))
                panel.TextLines.Add(Italic(listing.Description));

            panel.AddTabLine("Itinéraire GPS vers le bien", _ => AgentImmoPlugin.SetGps(player, listing.AreaId));
            panel.AddTabLine("Contacter l'agence (visite / achat)", _ => ContactAgency(player, listing));
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void ContactAgency(Player player, ImmoListing listing)
        {
            Panel panel = P.PanelHelper.Create("Contacter l'agence", UIPanel.PanelType.Input, player, () => ContactAgency(player, listing));
            panel.TextLines.Add($"Votre message pour l'agence {AgentImmoPlugin.BizName(listing.BizId)} :");
            panel.SetInputPlaceholder("Je souhaite visiter ce bien...");
            panel.PreviousButtonWithAction("Envoyer", async () =>
            {
                int characterId = player.character.Id;
                int listingId = listing.Id;
                if ((await ImmoRequest.Query(r => r.CharacterId == characterId && r.ListingId == listingId && r.Handled == false)).Count > 0)
                {
                    player.Notify(T, "Vous avez déjà une demande en attente pour ce bien.", NotificationManager.Type.Warning);
                    return false;
                }

                string message = string.IsNullOrWhiteSpace(panel.inputText) ? "Demande d'information" : panel.inputText.Trim();
                if (message.Length > 200) message = message.Substring(0, 200);

                await new ImmoRequest
                {
                    BizId = listing.BizId,
                    ListingId = listing.Id,
                    AreaId = listing.AreaId,
                    CharacterId = characterId,
                    CharacterName = player.FullName,
                    Message = message,
                    CreatedAt = AgentImmoPlugin.Now
                }.Save();

                foreach (Player agent in AgentImmoPlugin.OnlineEmployees(listing.BizId))
                    agent.Notify(T, $"Nouvelle demande de {player.FullName} pour le {AgentImmoPlugin.AreaLabel(listing.AreaId)} (AAMenu > Métier).", NotificationManager.Type.Info, 10f);

                player.Notify(T, "Demande envoyée : un agent vous recontactera.", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async void MyLeases(Player player)
        {
            int characterId = player.character.Id;
            List<ImmoLease> leases = await ImmoLease.Query(l => l.TenantCharacterId == characterId && l.Active == true);

            Panel panel = P.PanelHelper.Create("Mes locations", UIPanel.PanelType.TabPrice, player, () => MyLeases(player));
            if (leases.Count == 0)
                panel.AddTabLine("Vous n'avez aucune location", _ => { });
            foreach (ImmoLease lease in leases)
            {
                panel.AddTabLine($"{AgentImmoPlugin.AreaLabel(lease.AreaId)}<br>{Size(Color("Prochaine échéance : " + AgentImmoPlugin.Date(lease.NextDueAt), Colors.Grey), 14)}",
                    $"{AgentImmoPlugin.Money(lease.Rent)} / {lease.PeriodDays} j", IconUtils.Others.None.Id, _ => MyLeaseDetail(player, lease.Id));
            }
            if (leases.Count > 0)
                panel.NextButton("Ouvrir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async void MyLeaseDetail(Player player, int leaseId)
        {
            ImmoLease lease = await ImmoLease.Query(leaseId);
            if (lease == null || !lease.Active || lease.TenantCharacterId != player.character.Id)
            {
                player.Notify(T, "Ce bail n'est plus actif.", NotificationManager.Type.Warning);
                return;
            }

            Panel panel = P.PanelHelper.Create($"Mon bail n°{lease.Id}", UIPanel.PanelType.Tab, player, () => MyLeaseDetail(player, leaseId));
            panel.TextLines.Add(Bold(AgentImmoPlugin.AreaLabel(lease.AreaId)));
            panel.TextLines.Add($"Agence : {AgentImmoPlugin.BizName(lease.BizId)} - Propriétaire : {(lease.LandlordCharacterId == 0 ? "État" : lease.LandlordName)}");
            panel.TextLines.Add($"Loyer : {AgentImmoPlugin.Money(lease.Rent)} tous les {lease.PeriodDays} jour(s), prélevé automatiquement.");
            panel.TextLines.Add($"Prochaine échéance : {Color(AgentImmoPlugin.Date(lease.NextDueAt), lease.NextDueAt <= AgentImmoPlugin.Now ? Colors.Error : Colors.Success)}");

            panel.AddTabLine("GPS vers mon logement", _ => AgentImmoPlugin.SetGps(player, lease.AreaId));
            panel.AddTabLine($"Payer une période d'avance ({AgentImmoPlugin.Money(lease.Rent)})", async _ =>
            {
                if (lease.NextDueAt - AgentImmoPlugin.Now > 4L * Math.Max(1, lease.PeriodDays) * 86400)
                {
                    player.Notify(T, "Vous avez déjà payé plusieurs périodes d'avance.", NotificationManager.Type.Warning);
                    return;
                }
                if (!await P.PayRent(lease, player, "Loyer (paiement d'avance)"))
                    player.Notify(T, "Fonds insuffisants sur votre compte bancaire.", NotificationManager.Type.Error);
                panel.Refresh();
            });
            panel.AddTabLine(Color("Résilier mon bail", Colors.Error), _ => ConfirmTerminate(player, lease));
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void ConfirmTerminate(Player player, ImmoLease lease)
        {
            Panel panel = P.PanelHelper.Create("Résilier mon bail", UIPanel.PanelType.Text, player, () => ConfirmTerminate(player, lease));
            panel.TextLines.Add($"Voulez-vous vraiment quitter le {AgentImmoPlugin.AreaLabel(lease.AreaId)} ?");
            panel.TextLines.Add(Color("Les loyers déjà payés ne sont pas remboursés.", Colors.Warning));
            panel.CloseButtonWithAction("Confirmer", async () =>
            {
                await P.EndLease(lease, "résilié par le locataire", true);
                return true;
            });
            panel.PreviousButton();
            panel.Display();
        }

        private static async void MyMandates(Player player)
        {
            int characterId = player.character.Id;
            List<ImmoListing> listings = await ImmoListing.Query(l => l.OwnerCharacterId == characterId && l.Status == ImmoListing.Active);

            Panel panel = P.PanelHelper.Create("Mes biens en agence", UIPanel.PanelType.TabPrice, player, () => MyMandates(player));
            if (listings.Count == 0)
                panel.AddTabLine("Aucun bien confié à une agence", _ => { });
            foreach (ImmoListing listing in listings)
            {
                panel.AddTabLine($"[{listing.KindLabel}] {AgentImmoPlugin.AreaLabel(listing.AreaId)}<br>{Size(Color(AgentImmoPlugin.BizName(listing.BizId), Colors.Grey), 14)}",
                    listing.PriceLabel, IconUtils.Others.None.Id, async _ =>
                    {
                        listing.Status = ImmoListing.Removed;
                        await listing.Save();
                        await AgentImmoPlugin.Log(listing.BizId, listing.AreaId, "Mandat retiré par le propriétaire", 0, 0, player.FullName, "-");
                        player.Notify(T, "Annonce retirée : le mandat est terminé.", NotificationManager.Type.Success);
                        panel.Refresh();
                    });
            }
            if (listings.Count > 0)
                panel.AddButton(Color("Retirer l'annonce", Colors.Error), _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async void MyRentedOut(Player player)
        {
            int characterId = player.character.Id;
            List<ImmoLease> leases = await ImmoLease.Query(l => l.LandlordCharacterId == characterId && l.Active == true);

            Panel panel = P.PanelHelper.Create("Mes biens loués", UIPanel.PanelType.TabPrice, player, () => MyRentedOut(player));
            if (leases.Count == 0)
                panel.AddTabLine("Aucun bien loué", _ => { });
            foreach (ImmoLease lease in leases)
            {
                panel.AddTabLine($"{AgentImmoPlugin.AreaLabel(lease.AreaId)}<br>{Size(Color($"Locataire : {lease.TenantName} - échéance {AgentImmoPlugin.Date(lease.NextDueAt)}", Colors.Grey), 14)}",
                    $"{AgentImmoPlugin.Money(lease.Rent)} / {lease.PeriodDays} j", IconUtils.Others.None.Id, _ => { });
            }
            panel.TextLines.Add("Pour mettre fin à une location, contactez votre agence.");
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async void Agencies(Player player)
        {
            int activityId = await P.GetActivityId();
            List<CustomBiz> links = await ModEntity<CustomBiz>.Query(c => c.CustomActivityId == activityId);

            Panel panel = P.PanelHelper.Create("Agences immobilières", UIPanel.PanelType.Tab, player, () => Agencies(player));
            if (links.Count == 0)
                panel.AddTabLine("Aucune agence en ville", _ => { });
            foreach (CustomBiz link in links)
            {
                Bizs biz = Nova.biz.FetchBiz(link.BizId);
                if (biz == null) continue;
                int agents = AgentImmoPlugin.OnlineEmployees(biz.Id).Count;
                panel.AddTabLine($"{AgentImmoPlugin.BizName(biz.Id)}<br>{Size(Color($"{agents} agent(s) en ligne - contact : {biz.Contact ?? "-"}", Colors.Grey), 14)}", _ =>
                {
                    if (biz.TerrainId > 0)
                        AgentImmoPlugin.SetGps(player, biz.TerrainId);
                    else
                        player.Notify(T, "Adresse de l'agence inconnue.", NotificationManager.Type.Warning);
                });
            }
            if (links.Count > 0)
                panel.NextButton("GPS", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }

    // ======================================================================
    //  AAMenu > Administration : staff
    // ======================================================================

    public static class AdminMenu
    {
        private static AgentImmoPlugin P => AgentImmoPlugin.Instance;
        private const string T = "Agent Immo";

        private static bool CheckAdmin(Player player)
        {
            if (player != null && player.IsAdmin && player.serviceAdmin) return true;
            player?.Notify(T, "Vous devez être staff et en service admin.", NotificationManager.Type.Error);
            return false;
        }

        public static void Root(Player player)
        {
            if (!CheckAdmin(player)) return;

            Panel panel = P.PanelHelper.Create("Agent Immo - Administration", UIPanel.PanelType.Tab, player, () => Root(player));
            panel.TextLines.Add(Color("Agent Immo by Loris Strange", Colors.Purple));
            panel.AddTabLine("Agences immobilières (sociétés)", _ => Agencies(player));
            panel.AddTabLine("Paramètres", _ => Settings(player));
            panel.AddTabLine("Toutes les annonces", _ => AllListings(player));
            panel.AddTabLine("Toutes les locations", _ => AllLeases(player));
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.AddButton("Retour", _ => { panel.Close(); AAMenu.AAMenu.menu.AdminPanel(player); });
            panel.CloseButton();
            panel.Display();
        }

        private static async void Agencies(Player player)
        {
            int activityId = await P.GetActivityId();
            List<CustomBiz> links = await ModEntity<CustomBiz>.QueryAll();

            Panel panel = P.PanelHelper.Create("Agent Immo - Agences", UIPanel.PanelType.Tab, player, () => Agencies(player));
            panel.TextLines.Add("Cliquez sur une société pour en faire (ou non) une agence immobilière.");
            foreach (Bizs biz in Nova.biz.bizs.OrderBy(b => b.Id))
            {
                List<CustomBiz> bizLinks = links.Where(l => l.BizId == biz.Id).ToList();
                bool isAgency = bizLinks.Any(l => l.CustomActivityId == activityId);
                bool otherActivity = bizLinks.Any(l => l.CustomActivityId != activityId);
                string state = isAgency ? Color("[Agence]", Colors.Success) : otherActivity ? Color("[Autre activité]", Colors.Grey) : "";

                panel.AddTabLine($"[{biz.Id}] {BizUtils.BizNameWithoutId(biz.BizName ?? "")} {state}", async _ =>
                {
                    if (isAgency)
                    {
                        foreach (CustomBiz link in bizLinks.Where(l => l.CustomActivityId == activityId))
                            await link.Delete();
                        player.Notify(T, $"{biz.BizName} n'est plus une agence immobilière.", NotificationManager.Type.Success);
                    }
                    else if (otherActivity)
                    {
                        player.Notify(T, "Cette société a déjà une activité personnalisée (Administration > Activités personnalisées).", NotificationManager.Type.Warning);
                    }
                    else if (await P.JobHelper.AddCustomBiz(biz.Id, activityId))
                    {
                        player.Notify(T, $"{biz.BizName} est maintenant une agence immobilière.", NotificationManager.Type.Success);
                    }
                    else
                    {
                        player.Notify(T, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error);
                    }
                    panel.Refresh();
                });
            }
            panel.AddButton("Basculer", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void Settings(Player player)
        {
            ImmoConfig c = P.Config;
            Panel panel = P.PanelHelper.Create("Agent Immo - Paramètres", UIPanel.PanelType.Tab, player, () => Settings(player));
            panel.AddTabLine($"Commission sur les biens des joueurs : {c.CommissionPercent}%", _ =>
                EditNumber(player, "Commission (%)", c.CommissionPercent, 0, 100, v => c.CommissionPercent = v));
            panel.AddTabLine($"Part agence sur les terrains de l'État : {c.StateAgencyPercent}%", _ =>
                EditNumber(player, "Part agence État (%)", c.StateAgencyPercent, 0, 100, v => c.StateAgencyPercent = v));
            panel.AddTabLine($"Prime de l'agent (part de la commission) : {c.AgentBonusPercent}%", _ =>
                EditNumber(player, "Prime agent (%)", c.AgentBonusPercent, 0, 100, v => c.AgentBonusPercent = v));
            panel.AddTabLine($"Délai avant expulsion pour impayé : {c.RentGraceHours} h", _ =>
                EditNumber(player, "Délai d'impayé (heures)", c.RentGraceHours, 0, 720, v => c.RentGraceHours = (int)v));
            panel.AddTabLine($"Durée de location par défaut : {c.DefaultRentPeriodDays} j", _ =>
                EditNumber(player, "Période par défaut (jours)", c.DefaultRentPeriodDays, 1, 365, v => c.DefaultRentPeriodDays = (int)v));
            panel.AddTabLine($"Locations maximum par joueur : {(c.MaxLeasesPerPlayer == 0 ? "illimité" : c.MaxLeasesPerPlayer.ToString())}", _ =>
                EditNumber(player, "Locations max (0 = illimité)", c.MaxLeasesPerPlayer, 0, 100, v => c.MaxLeasesPerPlayer = (int)v));
            panel.AddTabLine($"Distance agent / client : {c.MaxClientDistance} m", _ =>
                EditNumber(player, "Distance (mètres)", c.MaxClientDistance, 1, 50, v => c.MaxClientDistance = (float)v));
            panel.AddTabLine($"Terrains de l'État vendus par les agences : {(c.AllowStateTerrains ? Color("Oui", Colors.Success) : Color("Non", Colors.Error))}", _ =>
            {
                c.AllowStateTerrains = !c.AllowStateTerrains;
                P.SaveConfig();
                panel.Refresh();
            });
            panel.AddTabLine($"Service obligatoire pour les agents : {(c.RequireService ? Color("Oui", Colors.Success) : Color("Non", Colors.Error))}", _ =>
            {
                c.RequireService = !c.RequireService;
                P.SaveConfig();
                panel.Refresh();
            });
            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void EditNumber(Player player, string label, double current, double min, double max, Action<double> apply)
        {
            Panel panel = P.PanelHelper.Create($"Agent Immo - {label}", UIPanel.PanelType.Input, player, () => EditNumber(player, label, current, min, max, apply));
            panel.TextLines.Add($"{label} : valeur actuelle {current} (entre {min} et {max})");
            panel.SetInputPlaceholder(current.ToString());
            panel.PreviousButtonWithAction("Valider", () =>
            {
                string input = panel.inputText?.Replace(",", ".").Trim();
                if (!double.TryParse(input, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
                    || value < min || value > max)
                {
                    player.Notify(T, $"Valeur invalide (entre {min} et {max}).", NotificationManager.Type.Error);
                    return Task.FromResult(false);
                }
                apply(value);
                P.SaveConfig();
                player.Notify(T, "Paramètre enregistré.", NotificationManager.Type.Success);
                return Task.FromResult(true);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async void AllListings(Player player)
        {
            List<ImmoListing> listings = (await ImmoListing.Query(l => l.Status == ImmoListing.Active)).OrderBy(l => l.BizId).ToList();

            Panel panel = P.PanelHelper.Create("Agent Immo - Annonces", UIPanel.PanelType.TabPrice, player, () => AllListings(player));
            if (listings.Count == 0)
                panel.AddTabLine("Aucune annonce", _ => { });
            foreach (ImmoListing listing in listings)
            {
                panel.AddTabLine($"[{listing.KindLabel}] {AgentImmoPlugin.AreaLabel(listing.AreaId)}<br>{Size(Color($"{AgentImmoPlugin.BizName(listing.BizId)} - propriétaire : {listing.OwnerLabel}", Colors.Grey), 14)}",
                    listing.PriceLabel, IconUtils.Others.None.Id, async _ =>
                    {
                        listing.Status = ImmoListing.Removed;
                        await listing.Save();
                        await AgentImmoPlugin.Log(listing.BizId, listing.AreaId, "Annonce retirée par le staff", 0, 0, listing.OwnerLabel, player.FullName);
                        player.Notify(T, "Annonce retirée.", NotificationManager.Type.Success);
                        panel.Refresh();
                    });
            }
            if (listings.Count > 0)
                panel.AddButton(Color("Retirer", Colors.Error), _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async void AllLeases(Player player)
        {
            List<ImmoLease> leases = (await ImmoLease.Query(l => l.Active == true)).OrderBy(l => l.NextDueAt).ToList();
            string action = "";

            Panel panel = P.PanelHelper.Create("Agent Immo - Locations", UIPanel.PanelType.TabPrice, player, () => AllLeases(player));
            if (leases.Count == 0)
                panel.AddTabLine("Aucune location", _ => { });
            foreach (ImmoLease lease in leases)
            {
                panel.AddTabLine($"{AgentImmoPlugin.AreaLabel(lease.AreaId)}<br>{Size(Color($"{lease.TenantName} - {AgentImmoPlugin.BizName(lease.BizId)} - échéance {AgentImmoPlugin.Date(lease.NextDueAt)}", Colors.Grey), 14)}",
                    $"{AgentImmoPlugin.Money(lease.Rent)} / {lease.PeriodDays} j", IconUtils.Others.None.Id, async _ =>
                    {
                        if (action == "gps")
                        {
                            AgentImmoPlugin.SetGps(player, lease.AreaId);
                            return;
                        }
                        await P.EndLease(lease, $"résilié par le staff ({player.FullName})", true);
                        player.Notify(T, "Bail résilié.", NotificationManager.Type.Success);
                        panel.Refresh();
                    });
            }
            if (leases.Count > 0)
            {
                panel.AddButton("GPS", _ => { action = "gps"; panel.SelectTab(); });
                panel.AddButton(Color("Résilier", Colors.Error), _ => { action = "end"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }
}
