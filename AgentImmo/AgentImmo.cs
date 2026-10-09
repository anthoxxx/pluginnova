using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Life;
using Life.BizSystem;
using Life.CheckpointSystem;
using Life.DB;
using Life.Network;
using Life.UI;
using ModKit.Helper;
using ModKit.Helper.DiscordHelper;
using ModKit.Helper.JobHelper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.ORM;
using Newtonsoft.Json;
using SQLite;
using Mathf = UnityEngine.Mathf;
using Vector3 = UnityEngine.Vector3;
using static ModKit.Helper.TextFormattingHelper;

namespace AgentImmo
{
    /// <summary>
    /// Agent Immo - By Loris Strange.
    /// Les entreprises « agent immobilier » gèrent un catalogue de terrains à vendre ou à louer,
    /// font des offres aux joueurs, encaissent les paiements et suivent tous les contrats.
    /// Toutes les ventes, locations, prolongations et fins de bail sont journalisées.
    /// </summary>
    public class AgentImmoPlugin : ModKit.ModKit
    {
        public const string Title = "Agent Immo";
        public const long SecondsPerDay = 86400;

        public ImmoConfig Config { get; private set; } = new ImmoConfig();
        private string _configPath;
        private int _minutes;

        public AgentImmoPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "Loris Strange");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            LoadConfig();

            Orm.RegisterTable<ImmoProperty>();
            Orm.RegisterTable<ImmoRental>();
            Orm.RegisterTable<ImmoLog>();
            Orm.RegisterTable<ImmoRequest>();

            RegisterMenus();
            RegisterCommands();
            _ = EnsureCustomActivity();

            Nova.server.OnMinutePassedEvent += OnMinutePassed;
            Nova.server.OnPlayerBuyTerrainEvent += OnPlayerBuyTerrain;

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", "initialisé (By Loris Strange)");
        }

        // ==================================================================
        //  Initialisation
        // ==================================================================

        private void LoadConfig()
        {
            try
            {
                string directory = Path.Combine(DirectoryPath, "AgentImmo");
                Directory.CreateDirectory(directory);
                _configPath = Path.Combine(directory, "config.json");

                if (File.Exists(_configPath))
                    Config = JsonConvert.DeserializeObject<ImmoConfig>(File.ReadAllText(_configPath)) ?? new ImmoConfig();

                // Réécrit le fichier pour y ajouter les nouvelles options éventuelles
                SaveConfig();
            }
            catch (Exception e)
            {
                Logger.LogError(PluginInformations.SourceName, $"Lecture de la configuration impossible : {e.Message}");
                Config = new ImmoConfig();
            }
        }

        public void SaveConfig()
        {
            try
            {
                File.WriteAllText(_configPath, JsonConvert.SerializeObject(Config, Formatting.Indented));
            }
            catch (Exception e)
            {
                Logger.LogError(PluginInformations.SourceName, $"Écriture de la configuration impossible : {e.Message}");
            }
        }

        /// <summary>Crée l'activité « Agent Immobilier » visible dans AAMenu (Administration → Activités).</summary>
        private async Task EnsureCustomActivity()
        {
            try
            {
                if (await JobHelper.AddCustomActivity(Config.ActivityName))
                    Logger.LogSuccess(PluginInformations.SourceName, $"Activité « {Config.ActivityName} » créée.");
            }
            catch (Exception e)
            {
                Logger.LogWarning(PluginInformations.SourceName, $"Activité « {Config.ActivityName} » non créée : {e.Message}");
            }
        }

        private void RegisterMenus()
        {
            if (AAMenu.AAMenu.menu == null)
            {
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : utilisez la commande /immo.");
                return;
            }

            // Tous les joueurs : catalogue, mes locations, mes achats (et l'espace agent pour les agents)
            AAMenu.Menu.AddInteractionTabLine(PluginInformations, "Agence immobilière", ui =>
            {
                Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                if (player != null) ClientMenu(player);
            });

            // Menu « Métier » des entreprises ayant l'activité « Agent Immobilier »
            AAMenu.Menu.AddBizTabLine(PluginInformations, new List<Activity.Type>(), new CustomActivity { Name = Config.ActivityName },
                "Agence immobilière", ui =>
                {
                    Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                    if (player != null) AgentMenu(player);
                });

            // Administration → Plugins
            AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, Config.AdminLevelMin, "Agent Immo", ui =>
            {
                Player player = PanelHelper.ReturnPlayerFromPanel(ui);
                if (player != null) AdminMenu(player);
            });
        }

        private void RegisterCommands()
        {
            new SChatCommand("/immo", "Ouvrir le menu de l'agence immobilière", "/immo",
                (Action<Player, string[]>)((player, args) => ClientMenu(player))).Register();
        }

        // ==================================================================
        //  Droits
        // ==================================================================

        /// <summary>Le joueur travaille-t-il dans une agence immobilière ?</summary>
        public async Task<bool> IsAgent(Player player)
        {
            if (player == null || !player.HasBiz || player.biz == null) return false;
            if (Config.AllowedBizIds.Contains(player.biz.Id)) return true;
            try
            {
                CustomActivity activity = await JobHelper.GetCustomActivityByPlayer(player);
                return activity != null && activity.Name == Config.ActivityName;
            }
            catch
            {
                return false;
            }
        }

        public bool IsStaff(Player player)
        {
            return player != null && player.IsAdmin && player.serviceAdmin && player.account.AdminLevel >= Config.AdminLevelMin;
        }

        /// <summary>Le joueur peut-il gérer ce bien ? (agent de l'agence propriétaire ou staff)</summary>
        private async Task<bool> CanManage(Player player, ImmoProperty property)
        {
            if (IsStaff(player)) return true;
            return await IsAgent(player) && player.biz.Id == property.BizId;
        }

        // ==================================================================
        //  Menu joueur
        // ==================================================================

        public async void ClientMenu(Player player)
        {
            bool isAgent = await IsAgent(player);

            Panel panel = PanelHelper.Create("Agence immobilière", UIPanel.PanelType.Tab, player, () => ClientMenu(player));
            panel.AddTabLine("Biens disponibles", _ => CatalogMenu(player));
            panel.AddTabLine("Mes locations", _ => MyRentalsMenu(player));
            panel.AddTabLine("Mes achats et locations (historique)", _ => LogsMenu(player, LogScope.Client(player.character.Id), null));
            if (isAgent)
                panel.AddTabLine(Color("Espace agent immobilier", Colors.Info), _ => AgentMenu(player));
            if (IsStaff(player))
                panel.AddTabLine(Color("Administration Agent Immo", Colors.Warning), _ => AdminMenu(player));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Catalogue de tous les biens disponibles, toutes agences confondues.</summary>
        public async void CatalogMenu(Player player)
        {
            List<ImmoProperty> properties = (await ImmoProperty.Query(p => p.StatusValue == (int)PropertyStatus.Disponible))
                .OrderBy(p => p.BizId).ThenBy(p => p.TerrainId).ToList();

            Panel panel = PanelHelper.Create("Biens disponibles", UIPanel.PanelType.Tab, player, () => CatalogMenu(player));
            if (properties.Count == 0)
                panel.AddTabLine("Aucun bien disponible pour le moment", _ => { });

            foreach (ImmoProperty property in properties)
            {
                panel.AddTabLine($"{property.Name} - {property.PriceLabel()} - {GetBizName(property.BizId)}", _ => ClientPropertyMenu(player, property));
            }

            if (properties.Count > 0)
                panel.NextButton("Voir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void ClientPropertyMenu(Player player, ImmoProperty property)
        {
            Panel panel = PanelHelper.Create(property.Name, UIPanel.PanelType.Tab, player, () => ClientPropertyMenu(player, property));
            panel.TextLines.Add($"Agence : {GetBizName(property.BizId)}");
            panel.TextLines.Add($"Terrain n°{property.TerrainId}");
            panel.TextLines.Add(property.PriceLabel());
            if (!string.IsNullOrEmpty(property.Description))
                panel.TextLines.Add(Italic(property.Description));

            if (property.HasPosition)
                panel.AddTabLine("Me guider jusqu'au terrain (GPS)", _ => SetGps(player, property));
            if (property.Mode != PropertyMode.Location)
                panel.AddTabLine("Je souhaite acheter ce bien", async _ => await CreateRequest(player, property, RequestKind.Achat));
            if (property.Mode != PropertyMode.Vente)
                panel.AddTabLine("Je souhaite louer ce bien", async _ => await CreateRequest(player, property, RequestKind.Location));
            panel.AddTabLine("Je souhaite visiter ce bien", async _ => await CreateRequest(player, property, RequestKind.Visite));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void SetGps(Player player, ImmoProperty property)
        {
            NCheckpoint checkpoint = null;
            checkpoint = new NCheckpoint(player.netId, property.Position, _ =>
            {
                player.DestroyCheckpoint(checkpoint);
                Notify(player, $"Vous êtes arrivé à « {property.Name} ».", NotificationManager.Type.Success);
            });
            player.CreateCheckpoint(checkpoint);
            Notify(player, "Le terrain a été indiqué sur votre GPS (point bleu).", NotificationManager.Type.Info);
        }

        /// <summary>Le joueur envoie une demande (achat, location, visite) à l'agence du bien.</summary>
        private async Task CreateRequest(Player player, ImmoProperty property, RequestKind kind)
        {
            int propertyId = property.Id;
            int characterId = player.character.Id;
            List<ImmoRequest> pending = await ImmoRequest.Query(r => r.PropertyId == propertyId && r.CharacterId == characterId && r.Handled == false);
            if (pending.Any(r => r.KindValue == (int)kind))
            {
                Notify(player, "Vous avez déjà envoyé cette demande, un agent va vous recontacter.", NotificationManager.Type.Warning);
                return;
            }

            ImmoRequest request = new ImmoRequest
            {
                PropertyId = property.Id,
                BizId = property.BizId,
                CharacterId = player.character.Id,
                PlayerName = player.FullName,
                KindValue = (int)kind,
                CreatedAt = Now(),
            };
            if (!await request.Save())
            {
                Notify(player, "Erreur lors de l'envoi de la demande.", NotificationManager.Type.Error);
                return;
            }

            int notified = 0;
            foreach (Player agent in GetOnlineAgents(property.BizId))
            {
                Notify(agent, $"Nouvelle demande de {player.FullName} : {kind.Label()} de « {property.Name} ».", NotificationManager.Type.Info);
                notified++;
            }
            Notify(player, notified > 0
                    ? $"Demande envoyée à l'agence ({notified} agent(s) en ligne)."
                    : "Demande envoyée. Aucun agent n'est en ligne, elle sera traitée plus tard.",
                NotificationManager.Type.Success);
        }

        public async void MyRentalsMenu(Player player)
        {
            int characterId = player.character.Id;
            List<ImmoRental> rentals = await ImmoRental.Query(r => r.TenantId == characterId && r.Active == true);

            Panel panel = PanelHelper.Create("Mes locations", UIPanel.PanelType.Tab, player, () => MyRentalsMenu(player));
            if (rentals.Count == 0)
                panel.AddTabLine("Vous n'avez aucune location en cours", _ => { });

            foreach (ImmoRental rental in rentals)
            {
                panel.AddTabLine($"{rental.PropertyName} (terrain n°{rental.TerrainId}) - fin : {FormatDate(rental.EndAt)}", _ => MyRentalMenu(player, rental));
            }

            if (rentals.Count > 0)
                panel.NextButton("Gérer", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void MyRentalMenu(Player player, ImmoRental rental)
        {
            Panel panel = PanelHelper.Create($"Location : {rental.PropertyName}", UIPanel.PanelType.Tab, player, () => MyRentalMenu(player, rental));
            panel.TextLines.Add($"Terrain n°{rental.TerrainId} - Agence : {GetBizName(rental.BizId)}");
            panel.TextLines.Add($"Loyer : {Money(rental.RentPerDay)} / jour");
            panel.TextLines.Add($"Fin du bail : {FormatDate(rental.EndAt)} ({FormatRemaining(rental.EndAt - Now())})");

            panel.AddTabLine("Prolonger la location", _ => ExtendRentalInput(player, rental));
            panel.AddTabLine(Color("Résilier le bail (sans remboursement)", Colors.Error), _ => ConfirmTerminate(player, rental));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void ExtendRentalInput(Player player, ImmoRental rental)
        {
            Panel panel = PanelHelper.Create("Prolonger la location", UIPanel.PanelType.Input, player, () => ExtendRentalInput(player, rental));
            panel.TextLines.Add($"Combien de jours voulez-vous ajouter ? ({Money(rental.RentPerDay)} / jour)");
            panel.TextLines.Add($"Durée maximale d'un bail : {Config.MaxRentDays} jours à partir d'aujourd'hui.");
            panel.SetInputPlaceholder("Nombre de jours");

            panel.PreviousButtonWithAction("Payer en espèces", async () => await ExtendRental(player, rental, panel.inputText, false));
            panel.PreviousButtonWithAction("Payer par carte", async () => await ExtendRental(player, rental, panel.inputText, true));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> ExtendRental(Player player, ImmoRental rental, string input, bool fromBank)
        {
            if (!int.TryParse(input, out int days) || days <= 0)
            {
                Notify(player, "Veuillez saisir un nombre de jours valide.", NotificationManager.Type.Error);
                return false;
            }

            ImmoRental current = await ImmoRental.Query(rental.Id);
            if (current == null || !current.Active)
            {
                Notify(player, "Cette location n'est plus active.", NotificationManager.Type.Error);
                return false;
            }

            long newEnd = Math.Max(current.EndAt, Now()) + days * SecondsPerDay;
            if (newEnd - Now() > Config.MaxRentDays * SecondsPerDay)
            {
                Notify(player, $"Un bail ne peut pas dépasser {Config.MaxRentDays} jours à l'avance.", NotificationManager.Type.Error);
                return false;
            }

            double total = current.RentPerDay * days;
            if (!GameBridge.TryDebit(player, total, fromBank, $"Prolongation location terrain {current.TerrainId}"))
            {
                Notify(player, $"Fonds insuffisants ({Money(total)} nécessaires).", NotificationManager.Type.Error);
                return false;
            }

            current.EndAt = newEnd;
            current.Warned = false;
            current.TotalPaid += total;
            await current.Save();

            string payment = PayAgency(current.BizId, total, null, "Prolongation de location");
            await AddLog(new ImmoLog
            {
                TypeValue = (int)LogType.Prolongation,
                PropertyId = current.PropertyId,
                PropertyName = current.PropertyName,
                TerrainId = current.TerrainId,
                BizId = current.BizId,
                ClientId = player.character.Id,
                ClientName = player.FullName,
                Amount = total,
                Details = $"+{days} jour(s), nouvelle fin : {FormatDate(newEnd)}. {payment}",
            });

            Notify(player, $"Location prolongée de {days} jour(s) jusqu'au {FormatDate(newEnd)}.", NotificationManager.Type.Success);
            foreach (Player agent in GetOnlineAgents(current.BizId))
                Notify(agent, $"{player.FullName} a prolongé la location de « {current.PropertyName} » ({Money(total)}).", NotificationManager.Type.Info);
            return true;
        }

        private void ConfirmTerminate(Player player, ImmoRental rental)
        {
            Panel panel = PanelHelper.Create("Résilier le bail", UIPanel.PanelType.Text, player, () => ConfirmTerminate(player, rental));
            panel.TextLines.Add($"Voulez-vous vraiment résilier la location de « {rental.PropertyName} » ?");
            panel.TextLines.Add(Color("Le terrain vous sera retiré immédiatement et les jours restants ne sont pas remboursés.", Colors.Warning));

            panel.CloseButtonWithAction("Confirmer", async () =>
            {
                ImmoRental current = await ImmoRental.Query(rental.Id);
                if (current == null || !current.Active) return true;
                await EndRental(current, LogType.Resiliation, $"Résiliée par le locataire {player.FullName}", player);
                return true;
            });
            panel.PreviousButton();
            panel.Display();
        }

        // ==================================================================
        //  Menu agent immobilier
        // ==================================================================

        public async void AgentMenu(Player player)
        {
            if (!await IsAgent(player))
            {
                Notify(player, "Vous devez travailler dans une agence immobilière.", NotificationManager.Type.Error);
                return;
            }

            int bizId = player.biz.Id;
            int pendingRequests = (await ImmoRequest.Query(r => r.BizId == bizId && r.Handled == false)).Count;
            int activeRentals = (await ImmoRental.Query(r => r.BizId == bizId && r.Active == true)).Count;

            Panel panel = PanelHelper.Create($"Agence : {GetBizName(bizId)}", UIPanel.PanelType.Tab, player, () => AgentMenu(player));
            panel.AddTabLine("Catalogue de l'agence", _ => AgencyCatalog(player, bizId));
            panel.AddTabLine("Ajouter un terrain au catalogue", _ => AddPropertyTerrain(player, new ImmoProperty { BizId = bizId }));
            panel.AddTabLine($"Demandes des clients ({pendingRequests})", _ => RequestsMenu(player, bizId));
            panel.AddTabLine($"Locations en cours ({activeRentals})", _ => RentalsMenu(player, bizId));
            panel.AddTabLine("Historique de l'agence", _ => LogsMenu(player, LogScope.Biz(bizId), null));
            panel.AddTabLine("Statistiques de l'agence", _ => StatsMenu(player, bizId));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Catalogue d'une agence (bizId) ou de toutes les agences (null, staff).</summary>
        public async void AgencyCatalog(Player player, int? bizId)
        {
            int agency = bizId ?? 0;
            List<ImmoProperty> properties = bizId.HasValue
                ? await ImmoProperty.Query(p => p.BizId == agency)
                : await ImmoProperty.QueryAll();
            properties = properties.OrderBy(p => p.StatusValue).ThenBy(p => p.TerrainId).ToList();

            string title = bizId.HasValue ? "Catalogue de l'agence" : "Tous les biens";
            Panel panel = PanelHelper.Create(title, UIPanel.PanelType.Tab, player, () => AgencyCatalog(player, bizId));
            if (properties.Count == 0)
                panel.AddTabLine("Aucun bien", _ => { });

            foreach (ImmoProperty property in properties)
            {
                string agencyName = bizId.HasValue ? "" : $" - {GetBizName(property.BizId)}";
                panel.AddTabLine($"{property.StatusLabel()} {property.Name} (terrain n°{property.TerrainId}){agencyName}", _ => AgentPropertyMenu(player, property));
            }

            if (properties.Count > 0)
                panel.NextButton("Gérer", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public async void AgentPropertyMenu(Player player, ImmoProperty property)
        {
            if (!await CanManage(player, property)) return;

            Panel panel = PanelHelper.Create($"Bien : {property.Name}", UIPanel.PanelType.Tab, player, async () =>
            {
                ImmoProperty fresh = await ImmoProperty.Query(property.Id);
                if (fresh != null) AgentPropertyMenu(player, fresh);
            });
            panel.TextLines.Add($"Terrain n°{property.TerrainId} - {property.StatusLabel()}");
            panel.TextLines.Add(property.PriceLabel());
            if (property.OwnerId != 0)
                panel.TextLines.Add($"Dernier acquéreur / locataire : {property.OwnerName}");

            if (property.Status == PropertyStatus.Disponible)
            {
                panel.AddTabLine(Color("Faire une offre à un joueur proche", Colors.Success), _ => ChooseNearbyClient(player, property));
                panel.AddTabLine("Retirer du catalogue", async _ =>
                {
                    property.Status = PropertyStatus.Retire;
                    await property.Save();
                    await AddLog(NewLog(LogType.Retrait, property, player, 0, "Bien retiré du catalogue"));
                    Notify(player, "Bien retiré du catalogue.", NotificationManager.Type.Success);
                    panel.Refresh();
                });
            }
            else if (property.Status == PropertyStatus.Vendu || property.Status == PropertyStatus.Retire)
            {
                panel.AddTabLine("Remettre en vente / location", async _ =>
                {
                    property.Status = PropertyStatus.Disponible;
                    await property.Save();
                    await AddLog(NewLog(LogType.RemiseEnVente, property, player, 0, "Bien remis dans le catalogue"));
                    Notify(player, "Le bien est de nouveau disponible.", NotificationManager.Type.Success);
                    panel.Refresh();
                });
            }

            panel.AddTabLine("Modifier le nom", _ => EditText(player, property, "Nom", property.Name, v => property.Name = v));
            panel.AddTabLine("Modifier la description", _ => EditText(player, property, "Description", property.Description, v => property.Description = v));
            panel.AddTabLine("Modifier le type (vente / location)", _ => AddPropertyMode(player, property, true));
            if (property.Mode != PropertyMode.Location)
                panel.AddTabLine("Modifier le prix de vente", _ => EditPrice(player, property, false));
            if (property.Mode != PropertyMode.Vente)
                panel.AddTabLine("Modifier le loyer journalier", _ => EditPrice(player, property, true));
            panel.AddTabLine("Définir le GPS à ma position", async _ =>
            {
                property.Position = player.setup.transform.position;
                await property.Save();
                Notify(player, "Position GPS du bien mise à jour.", NotificationManager.Type.Success);
            });
            if (property.HasPosition)
                panel.AddTabLine("Me guider jusqu'au terrain (GPS)", _ => SetGps(player, property));
            if (property.Status != PropertyStatus.Loue)
                panel.AddTabLine(Color("Supprimer définitivement", Colors.Error), _ => ConfirmDeleteProperty(player, property));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void ConfirmDeleteProperty(Player player, ImmoProperty property)
        {
            Panel panel = PanelHelper.Create("Supprimer le bien", UIPanel.PanelType.Text, player, () => ConfirmDeleteProperty(player, property));
            panel.TextLines.Add($"Supprimer définitivement « {property.Name} » du catalogue ?");
            panel.TextLines.Add("L'historique des transactions est conservé.");
            panel.CloseButtonWithAction(Color("Supprimer", Colors.Error), async () =>
            {
                if (!await property.Delete())
                {
                    Notify(player, "Erreur lors de la suppression.", NotificationManager.Type.Error);
                    return false;
                }
                await AddLog(NewLog(LogType.Suppression, property, player, 0, "Bien supprimé du catalogue"));
                Notify(player, "Bien supprimé.", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.Display();
        }

        private void EditText(Player player, ImmoProperty property, string field, string current, Action<string> apply)
        {
            Panel panel = PanelHelper.Create($"Modifier : {field}", UIPanel.PanelType.Input, player, () => EditText(player, property, field, current, apply));
            panel.TextLines.Add($"{field} actuel : {current}");
            panel.SetInputPlaceholder(field);
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string value = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(value))
                {
                    Notify(player, "La valeur ne peut pas être vide.", NotificationManager.Type.Error);
                    return false;
                }
                apply(value);
                await property.Save();
                Notify(player, $"{field} mis à jour.", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void EditPrice(Player player, ImmoProperty property, bool rent)
        {
            string label = rent ? "Loyer journalier" : "Prix de vente";
            Panel panel = PanelHelper.Create($"Modifier : {label}", UIPanel.PanelType.Input, player, () => EditPrice(player, property, rent));
            panel.TextLines.Add($"{label} actuel : {Money(rent ? property.RentPerDay : property.SalePrice)}");
            panel.SetInputPlaceholder("Montant en €");
            panel.PreviousButtonWithAction("Valider", async () =>
            {
                if (!TryParseAmount(panel.inputText, out double amount))
                {
                    Notify(player, "Montant invalide.", NotificationManager.Type.Error);
                    return false;
                }
                if (rent) property.RentPerDay = amount; else property.SalePrice = amount;
                await property.Save();
                await AddLog(NewLog(LogType.ModificationPrix, property, player, amount, $"{label} : {Money(amount)}"));
                Notify(player, $"{label} mis à jour : {Money(amount)}.", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---------------- Ajout d'un bien (assistant en plusieurs étapes) ----------------

        public void AddPropertyTerrain(Player player, ImmoProperty draft)
        {
            uint currentArea = player.setup.areaId;

            Panel panel = PanelHelper.Create("Nouveau bien (1/4) : terrain", UIPanel.PanelType.Input, player, () => AddPropertyTerrain(player, draft));
            panel.TextLines.Add("Numéro (ID) du terrain à mettre en vente ou en location.");
            if (currentArea != 0)
                panel.TextLines.Add($"Vous vous trouvez actuellement sur le terrain n°{currentArea}.");
            panel.TextLines.Add(Italic("Votre position actuelle servira de point GPS pour les clients."));
            panel.SetInputPlaceholder(currentArea != 0 ? currentArea.ToString() : "ID du terrain");

            panel.NextButton("Suivant", async () =>
            {
                string input = string.IsNullOrWhiteSpace(panel.inputText) && currentArea != 0 ? currentArea.ToString() : panel.inputText;
                if (!int.TryParse(input?.Trim(), out int terrainId) || terrainId <= 0)
                {
                    Notify(player, "ID de terrain invalide.", NotificationManager.Type.Error);
                    AddPropertyTerrain(player, draft);
                    return;
                }

                List<ImmoProperty> existing = await ImmoProperty.Query(p => p.TerrainId == terrainId);
                ImmoProperty active = existing.FirstOrDefault(p => p.Status == PropertyStatus.Disponible || p.Status == PropertyStatus.Loue);
                if (active != null)
                {
                    Notify(player, $"Ce terrain est déjà dans le catalogue de {GetBizName(active.BizId)} (« {active.Name} »).", NotificationManager.Type.Error);
                    AddPropertyTerrain(player, draft);
                    return;
                }

                draft.TerrainId = terrainId;
                draft.Position = player.setup.transform.position;
                AddPropertyName(player, draft);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void AddPropertyName(Player player, ImmoProperty draft)
        {
            Panel panel = PanelHelper.Create("Nouveau bien (2/4) : nom", UIPanel.PanelType.Input, player, () => AddPropertyName(player, draft));
            panel.TextLines.Add("Nom affiché aux clients (ex. « Villa avec piscine », « Entrepôt du port »).");
            panel.SetInputPlaceholder($"Terrain n°{draft.TerrainId}");

            panel.NextButton("Suivant", () =>
            {
                string name = panel.inputText?.Trim();
                draft.Name = string.IsNullOrEmpty(name) ? $"Terrain n°{draft.TerrainId}" : name;
                AddPropertyMode(player, draft, false);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Choix vente / location / les deux. Sert aussi à modifier un bien existant (edit = true).</summary>
        public void AddPropertyMode(Player player, ImmoProperty draft, bool edit)
        {
            Panel panel = PanelHelper.Create(edit ? "Modifier le type" : "Nouveau bien (3/4) : type", UIPanel.PanelType.Tab, player, () => AddPropertyMode(player, draft, edit));
            foreach (PropertyMode mode in new[] { PropertyMode.Vente, PropertyMode.Location, PropertyMode.VenteEtLocation })
            {
                panel.AddTabLine(mode.Label(), async _ =>
                {
                    draft.Mode = mode;
                    if (edit)
                    {
                        await draft.Save();
                        Notify(player, $"Type du bien : {mode.Label()}. Pensez à vérifier les prix.", NotificationManager.Type.Success);
                        panel.Previous();
                    }
                    else
                    {
                        AddPropertyPrices(player, draft);
                    }
                });
            }
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void AddPropertyPrices(Player player, ImmoProperty draft)
        {
            bool sale = draft.Mode != PropertyMode.Location;
            bool rent = draft.Mode != PropertyMode.Vente;

            Panel panel = PanelHelper.Create("Nouveau bien (4/4) : prix", UIPanel.PanelType.Input, player, () => AddPropertyPrices(player, draft));
            if (sale && rent)
            {
                panel.TextLines.Add("Indiquez le prix de vente et le loyer par jour séparés par « / ».");
                panel.SetInputPlaceholder("ex. 150000 / 500");
            }
            else
            {
                panel.TextLines.Add(sale ? "Prix de vente du terrain." : "Loyer par jour.");
                panel.SetInputPlaceholder("Montant en €");
            }
            panel.TextLines.Add($"Commission de l'agent sur chaque vente / location : {Config.CommissionPercent}%.");

            panel.CloseButtonWithAction("Créer le bien", async () =>
            {
                string[] parts = (panel.inputText ?? "").Split('/');
                double salePrice = 0, rentPrice = 0;
                bool valid = sale && rent
                    ? parts.Length == 2 && TryParseAmount(parts[0], out salePrice) && TryParseAmount(parts[1], out rentPrice)
                    : TryParseAmount(parts[0], out salePrice);
                if (!valid)
                {
                    Notify(player, "Montant(s) invalide(s).", NotificationManager.Type.Error);
                    return false;
                }
                if (!sale) { rentPrice = salePrice; salePrice = 0; }

                draft.SalePrice = salePrice;
                draft.RentPerDay = rentPrice;
                draft.Status = PropertyStatus.Disponible;
                draft.CreatedAt = Now();
                draft.CreatedBy = player.FullName;
                if (!await draft.Save())
                {
                    Notify(player, "Erreur lors de l'enregistrement du bien.", NotificationManager.Type.Error);
                    return false;
                }

                await AddLog(NewLog(LogType.Ajout, draft, player, 0, draft.PriceLabel()));
                Notify(player, $"« {draft.Name} » ajouté au catalogue.", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---------------- Offres ----------------

        /// <summary>L'agent choisit un joueur à proximité à qui proposer le bien.</summary>
        public void ChooseNearbyClient(Player agent, ImmoProperty property)
        {
            Vector3 position = agent.setup.transform.position;
            List<Player> nearby = Nova.server.Players
                .Where(p => p != agent && p.character != null && p.setup != null
                            && Vector3.Distance(p.setup.transform.position, position) <= Config.OfferDistance)
                .ToList();

            Panel panel = PanelHelper.Create("Choisir le client", UIPanel.PanelType.Tab, agent, () => ChooseNearbyClient(agent, property));
            panel.TextLines.Add($"Joueurs à moins de {Config.OfferDistance} m :");
            if (nearby.Count == 0)
                panel.AddTabLine("Aucun joueur à proximité", _ => { });
            foreach (Player client in nearby)
            {
                panel.AddTabLine(client.FullName, _ => ChooseOfferType(agent, client, property));
            }
            if (nearby.Count > 0)
                panel.NextButton("Choisir", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void ChooseOfferType(Player agent, Player client, ImmoProperty property)
        {
            if (property.Mode == PropertyMode.Vente)
            {
                SendOffer(agent, client, property, false, 0);
                return;
            }
            if (property.Mode == PropertyMode.Location)
            {
                AskRentDays(agent, client, property);
                return;
            }

            Panel panel = PanelHelper.Create("Type d'offre", UIPanel.PanelType.Tab, agent, () => ChooseOfferType(agent, client, property));
            panel.AddTabLine($"Vente - {Money(property.SalePrice)}", _ => SendOffer(agent, client, property, false, 0));
            panel.AddTabLine($"Location - {Money(property.RentPerDay)} / jour", _ => AskRentDays(agent, client, property));
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void AskRentDays(Player agent, Player client, ImmoProperty property)
        {
            Panel panel = PanelHelper.Create("Durée de la location", UIPanel.PanelType.Input, agent, () => AskRentDays(agent, client, property));
            panel.TextLines.Add($"Nombre de jours de location pour {client.FullName} ({Money(property.RentPerDay)} / jour, max {Config.MaxRentDays}).");
            panel.TextLines.Add(Italic("Le client paie toute la durée à la signature et pourra prolonger ensuite."));
            panel.SetInputPlaceholder("Nombre de jours");
            panel.NextButton("Envoyer l'offre", () =>
            {
                if (!int.TryParse(panel.inputText?.Trim(), out int days) || days <= 0 || days > Config.MaxRentDays)
                {
                    Notify(agent, $"Durée invalide (1 à {Config.MaxRentDays} jours).", NotificationManager.Type.Error);
                    AskRentDays(agent, client, property);
                    return;
                }
                SendOffer(agent, client, property, true, days);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Affiche l'offre au client, qui accepte (paiement espèces ou carte) ou refuse.</summary>
        public void SendOffer(Player agent, Player client, ImmoProperty property, bool isRent, int days)
        {
            double total = isRent ? property.RentPerDay * days : property.SalePrice;

            Panel panel = PanelHelper.Create("Offre immobilière", UIPanel.PanelType.Text, client, () => SendOffer(agent, client, property, isRent, days));
            panel.TextLines.Add($"{Bold(agent.FullName)} ({GetBizName(property.BizId)}) vous propose :");
            panel.TextLines.Add(isRent
                ? $"La {Bold("location")} de « {property.Name} » (terrain n°{property.TerrainId}) pendant {days} jour(s)."
                : $"L'{Bold("achat")} de « {property.Name} » (terrain n°{property.TerrainId}).");
            panel.TextLines.Add($"Montant total : {Color(Money(total), Colors.Success)}");
            if (!string.IsNullOrEmpty(property.Description))
                panel.TextLines.Add(Italic(property.Description));

            panel.CloseButtonWithAction("Payer en espèces", async () => await AcceptOffer(agent, client, property.Id, isRent, days, false));
            panel.CloseButtonWithAction("Payer par carte", async () => await AcceptOffer(agent, client, property.Id, isRent, days, true));
            panel.CloseButtonWithAction("Refuser", () =>
            {
                Notify(agent, $"{client.FullName} a refusé votre offre pour « {property.Name} ».", NotificationManager.Type.Warning);
                return Task.FromResult(true);
            });
            panel.Display();

            Notify(agent, $"Offre envoyée à {client.FullName} ({Money(total)}).", NotificationManager.Type.Info);
        }

        private async Task<bool> AcceptOffer(Player agent, Player client, int propertyId, bool isRent, int days, bool fromBank)
        {
            ImmoProperty property = await ImmoProperty.Query(propertyId);
            if (property == null || property.Status != PropertyStatus.Disponible)
            {
                Notify(client, "Ce bien n'est plus disponible.", NotificationManager.Type.Error);
                return true;
            }

            double total = isRent ? property.RentPerDay * days : property.SalePrice;
            string reason = $"{(isRent ? "Location" : "Achat")} terrain {property.TerrainId}";
            if (!GameBridge.TryDebit(client, total, fromBank, reason))
            {
                Notify(client, $"Fonds insuffisants ({Money(total)} nécessaires).", NotificationManager.Type.Error);
                Notify(agent, $"{client.FullName} n'a pas les fonds nécessaires.", NotificationManager.Type.Warning);
                return false;
            }

            // Encaissement : commission pour l'agent, le reste sur le compte de l'agence
            double commission = Math.Round(total * Config.CommissionPercent / 100.0, 2);
            string payment = PayAgency(property.BizId, total - commission, agent, reason);
            if (commission > 0 && !GameBridge.Credit(agent, commission, $"Commission {reason}"))
                payment += $" Commission de {Money(commission)} non versée à l'agent.";

            // Attribution du terrain dans le jeu
            int previousOwner = GameBridge.GetTerrainOwner(property.TerrainId) ?? 0;
            bool transferred = !Config.AutoTransferTerrain || GameBridge.SetTerrainOwner(property.TerrainId, client.character.Id);

            property.OwnerId = client.character.Id;
            property.OwnerName = client.FullName;
            property.Status = isRent ? PropertyStatus.Loue : PropertyStatus.Vendu;
            await property.Save();

            long endAt = 0;
            if (isRent)
            {
                endAt = Now() + days * SecondsPerDay;
                await new ImmoRental
                {
                    PropertyId = property.Id,
                    PropertyName = property.Name,
                    TerrainId = property.TerrainId,
                    BizId = property.BizId,
                    TenantId = client.character.Id,
                    TenantName = client.FullName,
                    AgentName = agent.FullName,
                    PreviousOwnerId = previousOwner,
                    RentPerDay = property.RentPerDay,
                    StartAt = Now(),
                    EndAt = endAt,
                    TotalPaid = total,
                    Active = true,
                }.Save();
            }

            // Les demandes de ce client pour ce bien sont traitées
            int clientId = client.character.Id;
            foreach (ImmoRequest request in await ImmoRequest.Query(r => r.PropertyId == propertyId && r.CharacterId == clientId && r.Handled == false))
            {
                request.Handled = true;
                await request.Save();
            }

            string transferText = transferred ? "" : $" ATTRIBUTION MANUELLE REQUISE (terrain n°{property.TerrainId}).";
            ImmoLog log = NewLog(isRent ? LogType.Location : LogType.Vente, property, agent, total,
                (isRent ? $"{days} jour(s), fin : {FormatDate(endAt)}. " : "") + payment + transferText);
            log.ClientId = client.character.Id;
            log.ClientName = client.FullName;
            log.Commission = commission;
            await AddLog(log);

            Notify(client, isRent
                    ? $"Vous louez « {property.Name} » jusqu'au {FormatDate(endAt)}."
                    : $"Félicitations, vous êtes propriétaire de « {property.Name} » !",
                NotificationManager.Type.Success);
            Notify(agent, $"{(isRent ? "Location" : "Vente")} conclue avec {client.FullName} : {Money(total)} (commission {Money(commission)}).", NotificationManager.Type.Success);

            if (!transferred)
            {
                const string manual = "Le terrain n'a pas pu être attribué automatiquement : contactez le staff.";
                Notify(client, manual, NotificationManager.Type.Warning);
                Notify(agent, manual, NotificationManager.Type.Warning);
            }
            return true;
        }

        /// <summary>Verse un montant sur le compte de l'agence, ou à l'agent si le compte est inaccessible.</summary>
        private string PayAgency(int bizId, double amount, Player agent, string reason)
        {
            if (amount <= 0) return "";
            if (GameBridge.CreditBiz(bizId, amount))
                return $"{Money(amount)} versés sur le compte de l'agence.";
            if (agent != null && GameBridge.Credit(agent, amount, reason))
                return $"Compte de l'agence inaccessible : {Money(amount)} versés à l'agent.";
            Logger.LogError(PluginInformations.SourceName, $"{Money(amount)} n'ont pas pu être versés à l'agence {bizId} ({reason}).");
            return $"{Money(amount)} NON VERSÉS (compte de l'agence inaccessible).";
        }

        // ---------------- Demandes des clients ----------------

        public async void RequestsMenu(Player player, int bizId)
        {
            List<ImmoRequest> requests = (await ImmoRequest.Query(r => r.BizId == bizId && r.Handled == false))
                .OrderByDescending(r => r.CreatedAt).ToList();
            Dictionary<int, ImmoProperty> properties = (await ImmoProperty.Query(p => p.BizId == bizId)).ToDictionary(p => p.Id);

            Panel panel = PanelHelper.Create("Demandes des clients", UIPanel.PanelType.Tab, player, () => RequestsMenu(player, bizId));
            if (requests.Count == 0)
                panel.AddTabLine("Aucune demande en attente", _ => { });

            foreach (ImmoRequest request in requests)
            {
                string propertyName = properties.TryGetValue(request.PropertyId, out ImmoProperty p) ? p.Name : "bien supprimé";
                panel.AddTabLine($"{FormatDate(request.CreatedAt)} - {request.PlayerName} : {request.Kind.Label()} de « {propertyName} »",
                    _ => RequestMenu(player, request, p));
            }

            if (requests.Count > 0)
                panel.NextButton("Traiter", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void RequestMenu(Player agent, ImmoRequest request, ImmoProperty property)
        {
            Player client = Nova.server.Players.FirstOrDefault(p => p.character != null && p.character.Id == request.CharacterId);

            Panel panel = PanelHelper.Create($"Demande de {request.PlayerName}", UIPanel.PanelType.Tab, agent, () => RequestMenu(agent, request, property));
            panel.TextLines.Add($"{request.Kind.Label()} de « {property?.Name ?? "bien supprimé"} » - le {FormatDate(request.CreatedAt)}");
            panel.TextLines.Add(client != null ? Color("Client en ligne", Colors.Success) : Color("Client hors ligne", Colors.Error));

            if (client != null && property != null && property.Status == PropertyStatus.Disponible)
            {
                panel.AddTabLine("Envoyer une offre au client (à distance)", _ => ChooseOfferType(agent, client, property));
                panel.AddTabLine("Prévenir le client que je le recontacte", _ =>
                    Notify(client, $"{agent.FullName} ({GetBizName(request.BizId)}) a bien reçu votre demande et va vous recontacter.", NotificationManager.Type.Info));
            }
            panel.AddTabLine("Marquer comme traitée", async _ =>
            {
                request.Handled = true;
                await request.Save();
                Notify(agent, "Demande marquée comme traitée.", NotificationManager.Type.Success);
                panel.Previous();
            });

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ---------------- Locations ----------------

        /// <summary>Locations actives d'une agence (bizId) ou de toutes (null, staff).</summary>
        public async void RentalsMenu(Player player, int? bizId)
        {
            int agency = bizId ?? 0;
            List<ImmoRental> rentals = bizId.HasValue
                ? await ImmoRental.Query(r => r.BizId == agency && r.Active == true)
                : await ImmoRental.Query(r => r.Active == true);
            rentals = rentals.OrderBy(r => r.EndAt).ToList();

            Panel panel = PanelHelper.Create("Locations en cours", UIPanel.PanelType.Tab, player, () => RentalsMenu(player, bizId));
            if (rentals.Count == 0)
                panel.AddTabLine("Aucune location en cours", _ => { });

            foreach (ImmoRental rental in rentals)
            {
                panel.AddTabLine($"{rental.PropertyName} - {rental.TenantName} - reste {FormatRemaining(rental.EndAt - Now())}",
                    _ => AgencyRentalMenu(player, rental));
            }

            if (rentals.Count > 0)
                panel.NextButton("Gérer", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void AgencyRentalMenu(Player player, ImmoRental rental)
        {
            Panel panel = PanelHelper.Create($"Location : {rental.PropertyName}", UIPanel.PanelType.Text, player, () => AgencyRentalMenu(player, rental));
            panel.TextLines.Add($"Locataire : {rental.TenantName} - Terrain n°{rental.TerrainId}");
            panel.TextLines.Add($"Signée le {FormatDate(rental.StartAt)} par {rental.AgentName}");
            panel.TextLines.Add($"Fin du bail : {FormatDate(rental.EndAt)} ({FormatRemaining(rental.EndAt - Now())})");
            panel.TextLines.Add($"Loyer : {Money(rental.RentPerDay)} / jour - Total payé : {Money(rental.TotalPaid)}");

            panel.CloseButtonWithAction(Color("Mettre fin au bail", Colors.Error), async () =>
            {
                ImmoRental current = await ImmoRental.Query(rental.Id);
                if (current == null || !current.Active) return true;
                await EndRental(current, LogType.Expulsion, $"Bail terminé par {player.FullName}", player);
                Notify(player, "Le bail a été terminé.", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Termine une location : le terrain revient à son ancien propriétaire et le bien redevient disponible.</summary>
        private async Task EndRental(ImmoRental rental, LogType type, string details, Player actor)
        {
            rental.Active = false;
            rental.EndedAt = Now();
            await rental.Save();

            bool restored = !Config.AutoTransferTerrain || GameBridge.SetTerrainOwner(rental.TerrainId, rental.PreviousOwnerId);

            ImmoProperty property = await ImmoProperty.Query(rental.PropertyId);
            if (property != null && property.Status == PropertyStatus.Loue)
            {
                property.Status = PropertyStatus.Disponible;
                await property.Save();
            }

            await AddLog(new ImmoLog
            {
                TypeValue = (int)type,
                PropertyId = rental.PropertyId,
                PropertyName = rental.PropertyName,
                TerrainId = rental.TerrainId,
                BizId = rental.BizId,
                ClientId = rental.TenantId,
                ClientName = rental.TenantName,
                AgentId = actor?.character?.Id ?? 0,
                AgentName = actor?.FullName ?? "Système",
                Details = details + (restored ? "" : $" RETRAIT MANUEL DU TERRAIN n°{rental.TerrainId} REQUIS."),
            });

            Player tenant = Nova.server.Players.FirstOrDefault(p => p.character != null && p.character.Id == rental.TenantId);
            if (tenant != null)
                Notify(tenant, $"Votre location de « {rental.PropertyName} » est terminée.", NotificationManager.Type.Warning);
            foreach (Player agent in GetOnlineAgents(rental.BizId))
                Notify(agent, $"Fin de location : « {rental.PropertyName} » ({rental.TenantName}).", NotificationManager.Type.Info);
        }

        // ==================================================================
        //  Historique et statistiques
        // ==================================================================

        public async void LogsMenu(Player player, LogScope scope, LogType? filter)
        {
            List<ImmoLog> logs = await scope.Query();
            if (filter.HasValue)
                logs = logs.Where(l => l.TypeValue == (int)filter.Value).ToList();
            int total = logs.Count;
            logs = logs.OrderByDescending(l => l.Date).Take(Config.MaxLogsDisplayed).ToList();

            string filterLabel = filter.HasValue ? filter.Value.Label() : "tout";
            Panel panel = PanelHelper.Create($"Historique ({filterLabel})", UIPanel.PanelType.Tab, player, () => LogsMenu(player, scope, filter));
            if (total > logs.Count)
                panel.TextLines.Add($"{logs.Count} dernières opérations sur {total}.");
            if (logs.Count == 0)
                panel.AddTabLine("Aucune opération", _ => { });

            foreach (ImmoLog log in logs)
            {
                string who = string.IsNullOrEmpty(log.ClientName) ? log.AgentName : log.ClientName;
                string amount = log.Amount > 0 ? $" - {Money(log.Amount)}" : "";
                panel.AddTabLine($"{FormatDate(log.Date)} - {log.Type.Label()} - {log.PropertyName} - {who}{amount}", _ => LogDetails(player, log));
            }

            if (logs.Count > 0)
                panel.NextButton("Détails", () => panel.SelectTab());
            panel.NextButton("Filtrer", () => LogsFilterMenu(player, scope));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void LogsFilterMenu(Player player, LogScope scope)
        {
            Panel panel = PanelHelper.Create("Filtrer l'historique", UIPanel.PanelType.Tab, player, () => LogsFilterMenu(player, scope));
            panel.AddTabLine("Tout afficher", _ => LogsMenu(player, scope, null));
            foreach (LogType type in Enum.GetValues(typeof(LogType)))
            {
                panel.AddTabLine(type.Label(), _ => LogsMenu(player, scope, type));
            }
            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void LogDetails(Player player, ImmoLog log)
        {
            Panel panel = PanelHelper.Create($"Opération #{log.Id}", UIPanel.PanelType.Text, player, () => LogDetails(player, log));
            panel.TextLines.Add($"{Bold(log.Type.Label())} le {FormatDate(log.Date)}");
            panel.TextLines.Add($"Bien : {log.PropertyName} (terrain n°{log.TerrainId})");
            panel.TextLines.Add($"Agence : {GetBizName(log.BizId)}");
            if (!string.IsNullOrEmpty(log.ClientName)) panel.TextLines.Add($"Client : {log.ClientName}");
            if (!string.IsNullOrEmpty(log.AgentName)) panel.TextLines.Add($"Agent : {log.AgentName}");
            if (log.Amount > 0) panel.TextLines.Add($"Montant : {Money(log.Amount)}");
            if (log.Commission > 0) panel.TextLines.Add($"Commission agent : {Money(log.Commission)}");
            if (!string.IsNullOrEmpty(log.Details)) panel.TextLines.Add(log.Details);
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Statistiques d'une agence (bizId) ou globales (null).</summary>
        public async void StatsMenu(Player player, int? bizId)
        {
            int agency = bizId ?? 0;
            List<ImmoLog> logs = await (bizId.HasValue ? LogScope.Biz(agency) : LogScope.All()).Query();
            List<ImmoProperty> properties = bizId.HasValue ? await ImmoProperty.Query(p => p.BizId == agency) : await ImmoProperty.QueryAll();
            List<ImmoRental> rentals = bizId.HasValue ? await ImmoRental.Query(r => r.BizId == agency && r.Active == true) : await ImmoRental.Query(r => r.Active == true);

            List<ImmoLog> sales = logs.Where(l => l.Type == LogType.Vente).ToList();
            List<ImmoLog> rents = logs.Where(l => l.Type == LogType.Location || l.Type == LogType.Prolongation).ToList();

            Panel panel = PanelHelper.Create(bizId.HasValue ? $"Statistiques : {GetBizName(bizId.Value)}" : "Statistiques globales",
                UIPanel.PanelType.Text, player, () => StatsMenu(player, bizId));
            panel.TextLines.Add($"Biens disponibles : {properties.Count(p => p.Status == PropertyStatus.Disponible)} - loués : {rentals.Count} - vendus : {properties.Count(p => p.Status == PropertyStatus.Vendu)}");
            panel.TextLines.Add($"Ventes : {sales.Count} pour {Money(sales.Sum(l => l.Amount))}");
            panel.TextLines.Add($"Locations et prolongations : {rents.Count} pour {Money(rents.Sum(l => l.Amount))}");
            panel.TextLines.Add($"Chiffre d'affaires total : {Bold(Money(sales.Sum(l => l.Amount) + rents.Sum(l => l.Amount)))}");
            panel.TextLines.Add($"Commissions versées aux agents : {Money(logs.Sum(l => l.Commission))}");

            ImmoLog bestAgent = logs.Where(l => l.Commission > 0).GroupBy(l => l.AgentName)
                .Select(g => new ImmoLog { AgentName = g.Key, Amount = g.Sum(l => l.Amount) })
                .OrderByDescending(l => l.Amount).FirstOrDefault();
            if (bestAgent != null)
                panel.TextLines.Add($"Meilleur agent : {bestAgent.AgentName} ({Money(bestAgent.Amount)})");

            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ==================================================================
        //  Administration
        // ==================================================================

        public void AdminMenu(Player player)
        {
            if (!IsStaff(player))
            {
                Notify(player, "Vous devez être staff et en service admin.", NotificationManager.Type.Error);
                return;
            }

            Panel panel = PanelHelper.Create("Agent Immo - Administration", UIPanel.PanelType.Tab, player, () => AdminMenu(player));
            panel.AddTabLine("Historique complet (toutes agences)", _ => LogsMenu(player, LogScope.All(), null));
            panel.AddTabLine("Tous les biens", _ => AgencyCatalog(player, null));
            panel.AddTabLine("Toutes les locations en cours", _ => RentalsMenu(player, null));
            panel.AddTabLine("Statistiques globales", _ => StatsMenu(player, null));
            panel.AddTabLine("Agences autorisées (par ID d'entreprise)", _ => AllowedBizMenu(player));
            panel.AddTabLine("Paramètres", _ => SettingsMenu(player));
            panel.AddTabLine("Diagnostic d'un terrain", _ => TerrainDiagnosticInput(player));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void AllowedBizMenu(Player player)
        {
            Panel panel = PanelHelper.Create("Agences autorisées", UIPanel.PanelType.Tab, player, () => AllowedBizMenu(player));
            panel.TextLines.Add($"Les entreprises ayant l'activité « {Config.ActivityName} » (AAMenu → Activités) sont aussi autorisées.");
            if (Config.AllowedBizIds.Count == 0)
                panel.AddTabLine("Aucune entreprise ajoutée manuellement", _ => { });
            foreach (int bizId in Config.AllowedBizIds.ToList())
            {
                panel.AddTabLine($"[{bizId}] {GetBizName(bizId)} - cliquer pour retirer", _ =>
                {
                    Config.AllowedBizIds.Remove(bizId);
                    SaveConfig();
                    Notify(player, $"{GetBizName(bizId)} retirée.", NotificationManager.Type.Success);
                    panel.Refresh();
                });
            }
            if (Config.AllowedBizIds.Count > 0)
                panel.NextButton("Retirer", () => panel.SelectTab());
            panel.NextButton("Ajouter", () => AddAllowedBiz(player));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void AddAllowedBiz(Player player)
        {
            Panel panel = PanelHelper.Create("Ajouter une agence", UIPanel.PanelType.Input, player, () => AddAllowedBiz(player));
            panel.TextLines.Add("ID de l'entreprise à autoriser comme agence immobilière.");
            if (player.HasBiz && player.biz != null)
                panel.TextLines.Add($"Votre entreprise : [{player.biz.Id}] {player.biz.BizName}");
            panel.SetInputPlaceholder("ID de l'entreprise");
            panel.PreviousButtonWithAction("Ajouter", () =>
            {
                if (!int.TryParse(panel.inputText?.Trim(), out int bizId) || GetBiz(bizId) == null)
                {
                    Notify(player, "Entreprise introuvable.", NotificationManager.Type.Error);
                    return Task.FromResult(false);
                }
                if (!Config.AllowedBizIds.Contains(bizId))
                {
                    Config.AllowedBizIds.Add(bizId);
                    SaveConfig();
                }
                Notify(player, $"{GetBizName(bizId)} est maintenant une agence immobilière.", NotificationManager.Type.Success);
                return Task.FromResult(true);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void SettingsMenu(Player player)
        {
            Panel panel = PanelHelper.Create("Paramètres", UIPanel.PanelType.Tab, player, () => SettingsMenu(player));
            panel.TextLines.Add($"Fichier : {_configPath}");
            panel.AddTabLine($"Commission des agents : {Config.CommissionPercent}%", _ =>
                EditSetting(player, "Commission des agents (%)", Config.CommissionPercent, v => Config.CommissionPercent = Mathf.Clamp(v, 0, 100)));
            panel.AddTabLine($"Durée maximale d'un bail : {Config.MaxRentDays} jours", _ =>
                EditSetting(player, "Durée maximale d'un bail (jours)", Config.MaxRentDays, v => Config.MaxRentDays = Math.Max(1, (int)v)));
            panel.AddTabLine($"Alerte avant fin de bail : {Config.ExpiryWarningHours} h", _ =>
                EditSetting(player, "Alerte avant fin de bail (heures)", Config.ExpiryWarningHours, v => Config.ExpiryWarningHours = Math.Max(0, (int)v)));
            panel.AddTabLine($"Distance max. pour une offre : {Config.OfferDistance} m", _ =>
                EditSetting(player, "Distance max. pour une offre (m)", Config.OfferDistance, v => Config.OfferDistance = Math.Max(1f, v)));
            panel.AddTabLine($"Attribution automatique des terrains : {(Config.AutoTransferTerrain ? "oui" : "non")}", _ =>
            {
                Config.AutoTransferTerrain = !Config.AutoTransferTerrain;
                SaveConfig();
                panel.Refresh();
            });
            panel.AddTabLine($"Webhook Discord : {(string.IsNullOrEmpty(Config.DiscordWebhookUrl) ? "désactivé" : "activé")}", _ => EditWebhook(player));
            panel.AddTabLine("Recharger le fichier de configuration", _ =>
            {
                LoadConfig();
                Notify(player, "Configuration rechargée.", NotificationManager.Type.Success);
                panel.Refresh();
            });

            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void EditSetting(Player player, string label, float current, Action<float> apply)
        {
            Panel panel = PanelHelper.Create(label, UIPanel.PanelType.Input, player, () => EditSetting(player, label, current, apply));
            panel.TextLines.Add($"Valeur actuelle : {current}");
            panel.SetInputPlaceholder(current.ToString(CultureInfo.InvariantCulture));
            panel.PreviousButtonWithAction("Valider", () =>
            {
                if (!float.TryParse(panel.inputText?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    Notify(player, "Valeur invalide.", NotificationManager.Type.Error);
                    return Task.FromResult(false);
                }
                apply(value);
                SaveConfig();
                return Task.FromResult(true);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void EditWebhook(Player player)
        {
            Panel panel = PanelHelper.Create("Webhook Discord", UIPanel.PanelType.Input, player, () => EditWebhook(player));
            panel.TextLines.Add("URL du webhook Discord qui recevra toutes les opérations (laisser vide pour désactiver).");
            panel.SetInputPlaceholder("https://discord.com/api/webhooks/...");
            panel.PreviousButtonWithAction("Valider", () =>
            {
                string url = panel.inputText?.Trim() ?? "";
                if (url.Length > 0 && !ModKit.Utils.InputUtils.IsValidDiscordWebhook(url))
                {
                    Notify(player, "URL de webhook invalide.", NotificationManager.Type.Error);
                    return Task.FromResult(false);
                }
                Config.DiscordWebhookUrl = url;
                SaveConfig();
                Notify(player, url.Length > 0 ? "Webhook enregistré." : "Webhook désactivé.", NotificationManager.Type.Success);
                return Task.FromResult(true);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void TerrainDiagnosticInput(Player player)
        {
            Panel panel = PanelHelper.Create("Diagnostic d'un terrain", UIPanel.PanelType.Input, player, () => TerrainDiagnosticInput(player));
            panel.TextLines.Add("Affiche ce que le plugin lit du terrain (propriétaire...). Le détail complet est écrit dans la console du serveur.");
            panel.SetInputPlaceholder(player.setup.areaId != 0 ? player.setup.areaId.ToString() : "ID du terrain");
            panel.NextButton("Analyser", () =>
            {
                string input = string.IsNullOrWhiteSpace(panel.inputText) ? player.setup.areaId.ToString() : panel.inputText.Trim();
                if (!int.TryParse(input, out int terrainId))
                {
                    Notify(player, "ID invalide.", NotificationManager.Type.Error);
                    TerrainDiagnosticInput(player);
                    return;
                }
                TerrainDiagnostic(player, terrainId);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public void TerrainDiagnostic(Player player, int terrainId)
        {
            string report = GameBridge.DescribeTerrain(terrainId);
            Logger.LogVerbose(PluginInformations.SourceName, $"Diagnostic terrain {terrainId} :\n{report}");

            Panel panel = PanelHelper.Create($"Terrain n°{terrainId}", UIPanel.PanelType.Text, player, () => TerrainDiagnostic(player, terrainId));
            int? owner = GameBridge.GetTerrainOwner(terrainId);
            panel.TextLines.Add(owner.HasValue ? $"Propriétaire (ID personnage) : {owner.Value}" : Color("Propriétaire illisible : voir la console.", Colors.Error));
            panel.TextLines.Add($"Argent lisible : {(GameBridge.GetMoney(player, false).HasValue ? "oui" : "non")} - Banque : {(GameBridge.GetMoney(player, true).HasValue ? "oui" : "non")}");
            foreach (string line in report.Split('\n').Take(12))
                panel.TextLines.Add(line);
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        // ==================================================================
        //  Évènements serveur
        // ==================================================================

        private async void OnMinutePassed()
        {
            try
            {
                long now = Now();
                foreach (ImmoRental rental in await ImmoRental.Query(r => r.Active == true))
                {
                    if (rental.EndAt <= now)
                    {
                        await EndRental(rental, LogType.FinLocation, "Fin du bail", null);
                    }
                    else if (!rental.Warned && rental.EndAt - now <= Config.ExpiryWarningHours * 3600L)
                    {
                        Player tenant = Nova.server.Players.FirstOrDefault(p => p.character != null && p.character.Id == rental.TenantId);
                        if (tenant == null) continue;
                        Notify(tenant, $"Votre location de « {rental.PropertyName} » se termine dans {FormatRemaining(rental.EndAt - now)}. Prolongez-la via /immo.",
                            NotificationManager.Type.Warning);
                        rental.Warned = true;
                        await rental.Save();
                    }
                }

                // Rappel aux agents des demandes en attente toutes les 30 minutes
                if (++_minutes % 30 == 0)
                {
                    List<ImmoRequest> pending = await ImmoRequest.Query(r => r.Handled == false);
                    foreach (IGrouping<int, ImmoRequest> group in pending.GroupBy(r => r.BizId))
                    {
                        foreach (Player agent in GetOnlineAgents(group.Key))
                            Notify(agent, $"{group.Count()} demande(s) client en attente à l'agence.", NotificationManager.Type.Info);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError(PluginInformations.SourceName, $"OnMinutePassed : {e.Message}");
            }
        }

        /// <summary>Achat direct d'un terrain dans le jeu (hors agence) : tracé dans l'historique pour un suivi complet.</summary>
        private async void OnPlayerBuyTerrain(Player player, int terrainId, double price)
        {
            try
            {
                ImmoProperty property = (await ImmoProperty.Query(p => p.TerrainId == terrainId))
                    .FirstOrDefault(p => p.Status == PropertyStatus.Disponible);
                if (property != null)
                {
                    property.Status = PropertyStatus.Vendu;
                    property.OwnerId = player.character.Id;
                    property.OwnerName = player.FullName;
                    await property.Save();
                }

                await AddLog(new ImmoLog
                {
                    TypeValue = (int)LogType.AchatDirect,
                    PropertyId = property?.Id ?? 0,
                    PropertyName = property?.Name ?? $"Terrain n°{terrainId}",
                    TerrainId = terrainId,
                    BizId = property?.BizId ?? 0,
                    ClientId = player.character.Id,
                    ClientName = player.FullName,
                    Amount = price,
                    Details = "Achat effectué directement dans le jeu, sans agent.",
                });
            }
            catch (Exception e)
            {
                Logger.LogError(PluginInformations.SourceName, $"OnPlayerBuyTerrain : {e.Message}");
            }
        }

        // ==================================================================
        //  Utilitaires
        // ==================================================================

        private static ImmoLog NewLog(LogType type, ImmoProperty property, Player agent, double amount, string details)
        {
            return new ImmoLog
            {
                TypeValue = (int)type,
                PropertyId = property.Id,
                PropertyName = property.Name,
                TerrainId = property.TerrainId,
                BizId = property.BizId,
                AgentId = agent?.character?.Id ?? 0,
                AgentName = agent?.FullName ?? "Système",
                Amount = amount,
                Details = details,
            };
        }

        /// <summary>Enregistre une opération dans l'historique et l'envoie sur Discord si configuré.</summary>
        private async Task AddLog(ImmoLog log)
        {
            log.Date = Now();
            log.BizName = GetBizName(log.BizId);
            if (!await log.Save())
                Logger.LogError(PluginInformations.SourceName, $"Impossible d'enregistrer l'opération : {log.Type.Label()} {log.PropertyName}");

            if (string.IsNullOrEmpty(Config.DiscordWebhookUrl)) return;
            try
            {
                List<string> names = new List<string> { "Bien", "Terrain", "Agence" };
                List<string> values = new List<string> { log.PropertyName ?? "-", log.TerrainId.ToString(), log.BizName ?? "-" };
                if (!string.IsNullOrEmpty(log.ClientName)) { names.Add("Client"); values.Add(log.ClientName); }
                if (!string.IsNullOrEmpty(log.AgentName)) { names.Add("Agent"); values.Add(log.AgentName); }
                if (log.Amount > 0) { names.Add("Montant"); values.Add(Money(log.Amount)); }
                if (log.Commission > 0) { names.Add("Commission"); values.Add(Money(log.Commission)); }

                await DiscordHelper.SendEmbed(new DiscordWebhookClient(Config.DiscordWebhookUrl), log.Type.DiscordColor(),
                    $"Agent Immo : {log.Type.Label()}", log.Details ?? "", names, values, true, true, "Agent Immo - By Loris Strange");
            }
            catch (Exception e)
            {
                Logger.LogWarning(PluginInformations.SourceName, $"Envoi Discord impossible : {e.Message}");
            }
        }

        public static IEnumerable<Player> GetOnlineAgents(int bizId)
        {
            return Nova.server.Players.Where(p => p.HasBiz && p.biz != null && p.biz.Id == bizId);
        }

        public static Bizs GetBiz(int bizId)
        {
            return Nova.biz.bizs?.FirstOrDefault(b => b.Id == bizId) ?? Nova.biz.FetchBiz(bizId);
        }

        public static string GetBizName(int bizId)
        {
            if (bizId == 0) return "Aucune agence";
            Bizs biz = GetBiz(bizId);
            return biz != null ? biz.BizName : $"Entreprise n°{bizId}";
        }

        public static void Notify(Player player, string message, NotificationManager.Type type)
        {
            player?.Notify(Title, message, type, 6f);
        }

        public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static string FormatDate(long unix) =>
            unix <= 0 ? "-" : DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("dd/MM/yyyy HH:mm");

        public static string FormatRemaining(long seconds)
        {
            if (seconds <= 0) return "terminé";
            TimeSpan span = TimeSpan.FromSeconds(seconds);
            if (span.TotalDays >= 1) return $"{(int)span.TotalDays} j {span.Hours} h";
            if (span.TotalHours >= 1) return $"{span.Hours} h {span.Minutes} min";
            return $"{Math.Max(1, span.Minutes)} min";
        }

        private static readonly NumberFormatInfo MoneyFormat = new NumberFormatInfo { NumberGroupSeparator = " ", NumberDecimalSeparator = "," };

        public static string Money(double amount) => amount.ToString("#,0.##", MoneyFormat) + " €";

        public static bool TryParseAmount(string input, out double amount)
        {
            string clean = (input ?? "").Replace(" ", "").Replace("€", "").Replace(',', '.');
            return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out amount) && amount > 0;
        }
    }

    // ======================================================================
    //  Configuration (Plugins/ModKit/AgentImmo/config.json)
    // ======================================================================

    public class ImmoConfig
    {
        /// <summary>Nom de l'activité AAMenu qui donne accès au menu agent.</summary>
        public string ActivityName { get; set; } = "Agent Immobilier";
        /// <summary>Entreprises autorisées en plus de celles qui ont l'activité.</summary>
        public List<int> AllowedBizIds { get; set; } = new List<int>();
        public float CommissionPercent { get; set; } = 10f;
        public int MaxRentDays { get; set; } = 30;
        public int ExpiryWarningHours { get; set; } = 24;
        public float OfferDistance { get; set; } = 5f;
        public int AdminLevelMin { get; set; } = 1;
        public int MaxLogsDisplayed { get; set; } = 100;
        /// <summary>Donne/retire automatiquement le terrain dans le jeu lors d'une vente ou location.</summary>
        public bool AutoTransferTerrain { get; set; } = true;
        public string DiscordWebhookUrl { get; set; } = "";
    }

    // ======================================================================
    //  Données (base SQLite de ModKit)
    // ======================================================================

    public enum PropertyMode { Vente = 0, Location = 1, VenteEtLocation = 2 }

    public enum PropertyStatus { Disponible = 0, Vendu = 1, Loue = 2, Retire = 3 }

    public enum RequestKind { Achat = 0, Location = 1, Visite = 2 }

    public enum LogType
    {
        Vente = 0, Location = 1, Prolongation = 2, FinLocation = 3, Resiliation = 4, Expulsion = 5,
        AchatDirect = 6, Ajout = 7, ModificationPrix = 8, Retrait = 9, RemiseEnVente = 10, Suppression = 11,
    }

    public static class ImmoLabels
    {
        public static string Label(this PropertyMode mode) => mode switch
        {
            PropertyMode.Vente => "Vente",
            PropertyMode.Location => "Location",
            _ => "Vente et location",
        };

        public static string Label(this RequestKind kind) => kind switch
        {
            RequestKind.Achat => "Achat",
            RequestKind.Location => "Location",
            _ => "Visite",
        };

        public static string Label(this LogType type) => type switch
        {
            LogType.Vente => "Vente",
            LogType.Location => "Location",
            LogType.Prolongation => "Prolongation",
            LogType.FinLocation => "Fin de location",
            LogType.Resiliation => "Résiliation",
            LogType.Expulsion => "Fin de bail (agence)",
            LogType.AchatDirect => "Achat direct (jeu)",
            LogType.Ajout => "Ajout au catalogue",
            LogType.ModificationPrix => "Modification de prix",
            LogType.Retrait => "Retrait du catalogue",
            LogType.RemiseEnVente => "Remise en vente",
            _ => "Suppression",
        };

        public static string DiscordColor(this LogType type) => type switch
        {
            LogType.Vente or LogType.AchatDirect => "#2ecc71",
            LogType.Location or LogType.Prolongation => "#3498db",
            LogType.FinLocation or LogType.Resiliation or LogType.Expulsion => "#e67e22",
            LogType.Suppression or LogType.Retrait => "#e74c3c",
            _ => "#95a5a6",
        };
    }

    /// <summary>Un terrain proposé à la vente et/ou à la location par une agence.</summary>
    public class ImmoProperty : ModEntity<ImmoProperty>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }
        public int TerrainId { get; set; }
        public int BizId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int ModeValue { get; set; }
        public int StatusValue { get; set; }
        public double SalePrice { get; set; }
        public double RentPerDay { get; set; }
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
        public int OwnerId { get; set; }
        public string OwnerName { get; set; }
        public string CreatedBy { get; set; }
        public long CreatedAt { get; set; }

        [Ignore]
        public PropertyMode Mode { get => (PropertyMode)ModeValue; set => ModeValue = (int)value; }

        [Ignore]
        public PropertyStatus Status { get => (PropertyStatus)StatusValue; set => StatusValue = (int)value; }

        [Ignore]
        public Vector3 Position
        {
            get => new Vector3(PosX, PosY, PosZ);
            set { PosX = value.x; PosY = value.y; PosZ = value.z; }
        }

        [Ignore]
        public bool HasPosition => PosX != 0 || PosY != 0 || PosZ != 0;

        public string PriceLabel() => Mode switch
        {
            PropertyMode.Vente => $"Vente : {AgentImmoPlugin.Money(SalePrice)}",
            PropertyMode.Location => $"Location : {AgentImmoPlugin.Money(RentPerDay)} / jour",
            _ => $"Vente : {AgentImmoPlugin.Money(SalePrice)} - Location : {AgentImmoPlugin.Money(RentPerDay)} / jour",
        };

        public string StatusLabel() => Status switch
        {
            PropertyStatus.Disponible => Color("[Disponible]", Colors.Success),
            PropertyStatus.Vendu => Color("[Vendu]", Colors.Error),
            PropertyStatus.Loue => Color("[Loué]", Colors.Info),
            _ => Color("[Retiré]", Colors.Warning),
        };
    }

    /// <summary>Contrat de location d'un terrain.</summary>
    public class ImmoRental : ModEntity<ImmoRental>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }
        public int PropertyId { get; set; }
        public string PropertyName { get; set; }
        public int TerrainId { get; set; }
        public int BizId { get; set; }
        public int TenantId { get; set; }
        public string TenantName { get; set; }
        public string AgentName { get; set; }
        /// <summary>Propriétaire du terrain avant la location, rétabli à la fin du bail.</summary>
        public int PreviousOwnerId { get; set; }
        public double RentPerDay { get; set; }
        public double TotalPaid { get; set; }
        public long StartAt { get; set; }
        public long EndAt { get; set; }
        public long EndedAt { get; set; }
        public bool Active { get; set; }
        public bool Warned { get; set; }
    }

    /// <summary>Une ligne de l'historique : vente, location, prolongation, fin de bail, modification...</summary>
    public class ImmoLog : ModEntity<ImmoLog>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }
        public long Date { get; set; }
        public int TypeValue { get; set; }
        public int PropertyId { get; set; }
        public string PropertyName { get; set; }
        public int TerrainId { get; set; }
        public int BizId { get; set; }
        public string BizName { get; set; }
        public int ClientId { get; set; }
        public string ClientName { get; set; }
        public int AgentId { get; set; }
        public string AgentName { get; set; }
        public double Amount { get; set; }
        public double Commission { get; set; }
        public string Details { get; set; }

        [Ignore]
        public LogType Type => (LogType)TypeValue;
    }

    /// <summary>Demande d'un joueur (achat, location, visite) adressée à une agence.</summary>
    public class ImmoRequest : ModEntity<ImmoRequest>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }
        public int PropertyId { get; set; }
        public int BizId { get; set; }
        public int CharacterId { get; set; }
        public string PlayerName { get; set; }
        public int KindValue { get; set; }
        public long CreatedAt { get; set; }
        public bool Handled { get; set; }

        [Ignore]
        public RequestKind Kind => (RequestKind)KindValue;
    }

    /// <summary>Périmètre d'affichage de l'historique : tout, une agence ou un client.</summary>
    public class LogScope
    {
        private int? _bizId;
        private int? _clientId;

        public static LogScope All() => new LogScope();
        public static LogScope Biz(int bizId) => new LogScope { _bizId = bizId };
        public static LogScope Client(int characterId) => new LogScope { _clientId = characterId };

        public async Task<List<ImmoLog>> Query()
        {
            if (_bizId.HasValue)
            {
                int bizId = _bizId.Value;
                return await ImmoLog.Query(l => l.BizId == bizId);
            }
            if (_clientId.HasValue)
            {
                int clientId = _clientId.Value;
                return await ImmoLog.Query(l => l.ClientId == clientId);
            }
            return await ImmoLog.QueryAll();
        }
    }

    // ======================================================================
    //  Accès au jeu par réflexion
    // ======================================================================

    /// <summary>
    /// Argent des joueurs, compte des entreprises et propriétaires des terrains.
    /// Ces membres du jeu ne sont pas exposés par ModKit : on y accède par réflexion
    /// (comme ModKit le fait pour NetworkAreaManager), ce qui évite que le plugin
    /// casse à la compilation si le jeu renomme un champ. En cas d'échec, l'opération
    /// est signalée dans l'historique et dans la console, et le diagnostic staff
    /// (Administration → Agent Immo → Diagnostic d'un terrain) affiche ce qui est lu.
    /// </summary>
    public static class GameBridge
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private static readonly string[] AreaGetters = { "GetAreaById", "GetArea", "GetLifeAreaById" };
        private static readonly string[] AreaIdNames = { "areaId", "AreaId", "id", "Id" };
        private static readonly string[] OwnerIdNames = { "characterId", "CharacterId", "ownerId", "OwnerId" };
        private static readonly string[] AreaSaveNames = { "Save", "SaveArea", "UpdateArea", "SaveAreaPermissions" };

        // ---------------- Argent ----------------

        public static double? GetMoney(Player player, bool bank)
        {
            object value = Get(player?.character, bank ? "Bank" : "Money");
            return value == null ? (double?)null : Convert.ToDouble(value);
        }

        /// <summary>Retire de l'argent au joueur (espèces ou banque). Faux si fonds insuffisants.</summary>
        public static bool TryDebit(Player player, double amount, bool bank, string reason)
        {
            if (amount <= 0) return true;
            double? current = GetMoney(player, bank);
            if (!current.HasValue)
            {
                Logger.LogError(AgentImmoPlugin.Title, $"Impossible de lire l'argent ({(bank ? "Bank" : "Money")}) du joueur.");
                return false;
            }
            if (current.Value < amount) return false;

            if (!bank && TryInvoke(player, "AddMoney", out _, -amount, reason)) return true;
            if (!Set(player.character, bank ? "Bank" : "Money", current.Value - amount)) return false;
            TryInvoke(player.character, "Save", out _);
            return true;
        }

        public static bool Credit(Player player, double amount, string reason)
        {
            if (player == null || amount <= 0) return amount <= 0;
            if (TryInvoke(player, "AddMoney", out _, amount, reason)) return true;
            double? current = GetMoney(player, false);
            if (!current.HasValue || !Set(player.character, "Money", current.Value + amount)) return false;
            TryInvoke(player.character, "Save", out _);
            return true;
        }

        /// <summary>Crédite le compte en banque d'une entreprise.</summary>
        public static bool CreditBiz(int bizId, double amount)
        {
            Bizs biz = AgentImmoPlugin.GetBiz(bizId);
            object bank = Get(biz, "Bank");
            if (bank == null || !Set(biz, "Bank", Convert.ToDouble(bank) + amount)) return false;
            TryInvoke(biz, "Save", out _);
            return true;
        }

        // ---------------- Terrains ----------------

        public static object GetAreaManager() => Get(typeof(Nova), "a");

        public static object GetArea(int areaId)
        {
            object manager = GetAreaManager();
            if (manager == null) return null;

            foreach (string getter in AreaGetters)
            {
                if (TryInvoke(manager, getter, out object area, areaId) && area != null) return area;
            }

            // Sinon : recherche dans les listes / dictionnaires du gestionnaire
            foreach (MemberInfo member in manager.GetType().GetMembers(Flags))
            {
                object value = member is FieldInfo f ? Safe(() => f.GetValue(f.IsStatic ? null : manager))
                    : member is PropertyInfo p && p.GetIndexParameters().Length == 0 ? Safe(() => p.GetValue(manager))
                    : null;
                IEnumerable items = value is IDictionary dictionary ? dictionary.Values : value as IEnumerable;
                if (items == null || value is string) continue;

                foreach (object item in items)
                {
                    object id = AreaIdNames.Select(n => Get(item, n)).FirstOrDefault(v => v != null);
                    if (id != null && IsNumber(id) && Convert.ToInt64(id) == areaId) return item;
                }
            }
            return null;
        }

        private static object GetOwnerEntity(object area, out object permissions)
        {
            permissions = Get(area, "permissions") ?? Get(area, "Permissions");
            return Get(permissions, "owner") ?? Get(permissions, "Owner");
        }

        public static int? GetTerrainOwner(int areaId)
        {
            object owner = GetOwnerEntity(GetArea(areaId), out _);
            object id = OwnerIdNames.Select(n => Get(owner, n)).FirstOrDefault(v => v != null);
            return id != null && IsNumber(id) ? Convert.ToInt32(id) : (int?)null;
        }

        /// <summary>Change le propriétaire d'un terrain (0 = aucun). Faux si le terrain n'a pas pu être modifié.</summary>
        public static bool SetTerrainOwner(int areaId, int characterId)
        {
            try
            {
                object area = GetArea(areaId);
                object owner = GetOwnerEntity(area, out object permissions);
                string idName = OwnerIdNames.FirstOrDefault(n => Get(owner, n) != null);
                if (area == null || owner == null || idName == null || !Set(owner, idName, characterId))
                {
                    Logger.LogError(AgentImmoPlugin.Title, $"Terrain {areaId} : propriétaire introuvable, attribution manuelle nécessaire.");
                    return false;
                }
                if (Get(owner, "groupId") != null) Set(owner, "groupId", 0);

                // Recopie au cas où ce sont des structures (copiées par valeur)
                Set(permissions, "owner", owner);
                Set(area, "permissions", permissions);

                object manager = GetAreaManager();
                bool saved = AreaSaveNames.Any(n => TryInvoke(area, n, out _))
                             || AreaSaveNames.Any(n => TryInvoke(manager, n, out _, area))
                             || AreaSaveNames.Any(n => TryInvoke(manager, n, out _, areaId));
                if (!saved)
                    Logger.LogWarning(AgentImmoPlugin.Title, $"Terrain {areaId} : propriétaire modifié, aucune méthode de sauvegarde trouvée.");
                return true;
            }
            catch (Exception e)
            {
                Logger.LogError(AgentImmoPlugin.Title, $"Terrain {areaId} : {e.Message}");
                return false;
            }
        }

        public static string DescribeTerrain(int areaId)
        {
            object manager = GetAreaManager();
            if (manager == null) return "Nova.a introuvable.";
            object area = GetArea(areaId);
            if (area == null) return $"Gestionnaire {manager.GetType().FullName} trouvé, mais aucun terrain n°{areaId}.";

            StringBuilder builder = new StringBuilder();
            Describe(area, builder, "", 0);
            return builder.ToString();
        }

        private static void Describe(object obj, StringBuilder builder, string indent, int depth)
        {
            if (obj == null) return;
            builder.Append(indent).Append('[').Append(obj.GetType().FullName).Append("]\n");
            foreach (MemberInfo member in obj.GetType().GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                object value;
                if (member is FieldInfo f && !f.Name.Contains("<")) value = Safe(() => f.GetValue(obj));
                else if (member is PropertyInfo p && p.GetIndexParameters().Length == 0) value = Safe(() => p.GetValue(obj));
                else continue;

                if (value == null || value is string || value.GetType().IsPrimitive || value is Enum)
                    builder.Append(indent).Append("  ").Append(member.Name).Append(" = ").Append(value ?? "null").Append('\n');
                else if (depth < 2 && member.Name.IndexOf("perm", StringComparison.OrdinalIgnoreCase) >= 0
                         || depth == 1 && member.Name.IndexOf("owner", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    builder.Append(indent).Append("  ").Append(member.Name).Append(":\n");
                    Describe(value, builder, indent + "    ", depth + 1);
                }
            }
        }

        // ---------------- Réflexion ----------------

        private static object Get(object target, string name)
        {
            if (target == null) return null;
            Type type = target as Type ?? target.GetType();
            object instance = target is Type ? null : target;
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo field = t.GetField(name, Flags | BindingFlags.DeclaredOnly);
                if (field != null) return Safe(() => field.GetValue(field.IsStatic ? null : instance));
                PropertyInfo property = Safe(() => t.GetProperty(name, Flags | BindingFlags.DeclaredOnly));
                if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                    return Safe(() => property.GetValue(instance));
            }
            return null;
        }

        private static bool Set(object target, string name, object value)
        {
            if (target == null) return false;
            for (Type t = target.GetType(); t != null; t = t.BaseType)
            {
                try
                {
                    FieldInfo field = t.GetField(name, Flags | BindingFlags.DeclaredOnly);
                    if (field != null && !field.IsInitOnly)
                    {
                        field.SetValue(target, ConvertTo(value, field.FieldType));
                        return true;
                    }
                    PropertyInfo property = t.GetProperty(name, Flags | BindingFlags.DeclaredOnly);
                    if (property != null && property.CanWrite)
                    {
                        property.SetValue(target, ConvertTo(value, property.PropertyType));
                        return true;
                    }
                }
                catch (Exception e)
                {
                    Logger.LogVerbose(AgentImmoPlugin.Title, $"Set {name} : {e.Message}");
                    return false;
                }
            }
            return false;
        }

        private static bool TryInvoke(object target, string name, out object result, params object[] args)
        {
            result = null;
            if (target == null) return false;
            foreach (MethodInfo method in target.GetType().GetMethods(Flags).Where(m => m.Name == name && m.GetParameters().Length == args.Length))
            {
                try
                {
                    ParameterInfo[] parameters = method.GetParameters();
                    object[] converted = new object[args.Length];
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (args[i] != null && !parameters[i].ParameterType.IsInstanceOfType(args[i]) && !(args[i] is IConvertible))
                            throw new InvalidCastException();
                        converted[i] = ConvertTo(args[i], parameters[i].ParameterType);
                    }
                    result = method.Invoke(method.IsStatic ? null : target, converted);
                    return true;
                }
                catch (InvalidCastException) { }
                catch (FormatException) { }
                catch (Exception e)
                {
                    Logger.LogVerbose(AgentImmoPlugin.Title, $"{target.GetType().Name}.{name} : {(e.InnerException ?? e).Message}");
                    return false;
                }
            }
            return false;
        }

        private static object ConvertTo(object value, Type type)
        {
            if (value == null || type.IsInstanceOfType(value)) return value;
            if (type.IsEnum) return Enum.ToObject(type, value);
            return Convert.ChangeType(value, Nullable.GetUnderlyingType(type) ?? type, CultureInfo.InvariantCulture);
        }

        private static bool IsNumber(object value) => value is int || value is uint || value is long || value is short || value is ushort || value is byte;

        private static T Safe<T>(Func<T> func)
        {
            try { return func(); }
            catch { return default; }
        }
    }
}
