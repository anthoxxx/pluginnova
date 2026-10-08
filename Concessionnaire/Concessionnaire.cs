using System;
using System.Collections.Generic;
using System.Globalization;
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
using Newtonsoft.Json;
using SQLite;
using static ModKit.Helper.TextFormattingHelper;

namespace Concessionnaire
{
    /// <summary>
    /// Concessionnaire by Loris Strange : le staff crée autant de concessionnaires
    /// qu'il veut, configure leur catalogue de véhicules (modèle, nom, prix, stock,
    /// moyens de paiement) en jeu, puis place des points bleus où les joueurs
    /// achètent les véhicules.
    /// </summary>
    public class ConcessionnairePlugin : ModKit.ModKit
    {
        public const string Title = "Concessionnaire";

        public ConcessionnairePlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "Loris Strange");
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            Orm.RegisterTable<DealershipPattern>();
            Orm.RegisterTable<DealershipVehicle>();

            // Le même modèle sert au PointHelper (déclenchement des points)
            // et à AAMenu (Administration > Points bleus).
            DealershipPattern pattern = new DealershipPattern(false);
            PointHelper.AddPattern(nameof(DealershipPattern), pattern);
            if (AAMenu.AAMenu.menu != null)
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, Title, pattern, this);
            else
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : utilisez la commande /concess.");

            RegisterCommands();

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version} by Loris Strange", "initialisé");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            // Affiche au joueur tous les concessionnaires placés
            PointHelper.InitAllNPoint(player);
        }

        private void RegisterCommands()
        {
            // Raccourci staff pour ouvrir directement la gestion des concessionnaires
            SChatCommand command = new SChatCommand("/concess", new string[] { "/concessionnaire" },
                "Gérer les concessionnaires (staff)", "/concess",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!player.IsAdmin || !player.serviceAdmin)
                    {
                        player.Notify(Title, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
                        return;
                    }
                    new DealershipPattern(false) { Context = this }.CreateOrGenerate(player);
                }));
            command.Register();
        }
    }
}

namespace Concessionnaire
{
    /// <summary>Un véhicule en vente dans un concessionnaire.</summary>
    public class DealershipVehicle : ModEntity<DealershipVehicle>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        /// <summary>Id du <see cref="DealershipPattern"/> qui vend ce véhicule.</summary>
        public int DealershipId { get; set; }

        /// <summary>Id du modèle de véhicule Nova-Life.</summary>
        public int ModelId { get; set; }

        /// <summary>Nom affiché aux joueurs.</summary>
        public string Label { get; set; }

        public double Price { get; set; }

        /// <summary>Nombre d'exemplaires restants, -1 = illimité.</summary>
        public int Stock { get; set; } = -1;

        public DealershipVehicle() { }
    }

    /// <summary>
    /// Modèle de concessionnaire. Un modèle possède son propre catalogue et peut être
    /// placé autant de fois que voulu : chaque placement crée un point bleu (NPoint).
    /// </summary>
    public class DealershipPattern : ModEntity<DealershipPattern>, PatternData
    {
        private const string Title = ConcessionnairePlugin.Title;

        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PatternName { get; set; }

        public bool AllowCash { get; set; } = true;

        public bool AllowBank { get; set; } = true;

        [Ignore]
        public string TypeName { get; set; } = nameof(DealershipPattern);

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        public DealershipPattern() { }

        public DealershipPattern(bool isCreated)
        {
            TypeName = nameof(DealershipPattern);
        }

        private static string FormatPrice(double price)
        {
            return price.ToString("N0", CultureInfo.GetCultureInfo("fr-FR")) + " €";
        }

        private static string FormatStock(int stock)
        {
            return stock < 0 ? "illimité" : stock.ToString();
        }

        private static bool TryParsePrice(string text, out double price)
        {
            text = text?.Replace(" ", "").Replace("€", "").Replace(",", ".");
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out price) && price >= 0;
        }

        private void Notify(Player player, string message, NotificationManager.Type type)
        {
            player.Notify(Title, message, type, 5f);
        }

        // ------------------------------------------------------------------
        //  Côté joueur : catalogue et achat
        // ------------------------------------------------------------------

        public void OnPlayerTrigger(Player player)
        {
            OpenCatalogue(player);
        }

        private async void OpenCatalogue(Player player)
        {
            List<DealershipVehicle> vehicles = await DealershipVehicle.Query(v => v.DealershipId == Id);

            Panel panel = Context.PanelHelper.Create(PatternName ?? Title, UIPanel.PanelType.Tab, player, () => OpenCatalogue(player));

            if (vehicles.Count == 0)
            {
                panel.AddTabLine("Aucun véhicule en vente pour le moment", _ => { });
            }
            foreach (DealershipVehicle vehicle in vehicles.OrderBy(v => v.Price))
            {
                string line = $"{vehicle.Label} - {FormatPrice(vehicle.Price)}";
                if (vehicle.Stock == 0) line = Color($"{line} (rupture de stock)", Colors.Error);
                else if (vehicle.Stock > 0) line += $" ({vehicle.Stock} en stock)";

                panel.AddTabLine(line, _ => ShowVehicle(player, vehicle));
            }

            if (vehicles.Count > 0)
                panel.NextButton("Voir", () => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        private void ShowVehicle(Player player, DealershipVehicle vehicle)
        {
            Panel panel = Context.PanelHelper.Create($"{PatternName} - {vehicle.Label}", UIPanel.PanelType.Text, player, () => ShowVehicle(player, vehicle));
            panel.TextLines.Add($"Véhicule : {vehicle.Label}");
            panel.TextLines.Add($"Prix : {Color(FormatPrice(vehicle.Price), Colors.Warning)}");
            panel.TextLines.Add($"Stock : {FormatStock(vehicle.Stock)}");
            panel.TextLines.Add($"Votre argent : {FormatPrice(player.character.Money)} | Banque : {FormatPrice(player.character.Bank)}");

            if (vehicle.Stock != 0)
            {
                if (AllowCash)
                    panel.PreviousButtonWithAction("Payer en liquide", async () => await Buy(player, vehicle.Id, false));
                if (AllowBank)
                    panel.PreviousButtonWithAction("Payer par banque", async () => await Buy(player, vehicle.Id, true));
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> Buy(Player player, int vehicleEntryId, bool bank)
        {
            // Relit l'entrée pour avoir le prix et le stock à jour
            DealershipVehicle vehicle = await DealershipVehicle.Query(vehicleEntryId);
            if (vehicle == null || vehicle.DealershipId != Id)
            {
                Notify(player, "Ce véhicule n'est plus en vente.", NotificationManager.Type.Error);
                return false;
            }
            if (vehicle.Stock == 0)
            {
                Notify(player, "Ce véhicule est en rupture de stock.", NotificationManager.Type.Error);
                return false;
            }
            if ((bank && !AllowBank) || (!bank && !AllowCash))
            {
                Notify(player, "Ce moyen de paiement n'est pas accepté ici.", NotificationManager.Type.Error);
                return false;
            }

            double balance = bank ? player.character.Bank : player.character.Money;
            if (balance < vehicle.Price)
            {
                Notify(player, $"Fonds insuffisants ({FormatPrice(vehicle.Price)} requis).", NotificationManager.Type.Error);
                return false;
            }

            Pay(player, -vehicle.Price, bank, $"Achat {vehicle.Label}");

            Life.PermissionSystem.Permissions permissions = new Life.PermissionSystem.Permissions
            {
                owner = new Life.PermissionSystem.Entity { characterId = player.character.Id },
                coOwners = new List<Life.PermissionSystem.Entity>()
            };
            int vehicleId = await LifeDB.CreateVehicle(vehicle.ModelId, JsonConvert.SerializeObject(permissions));

            if (vehicleId <= 0)
            {
                Pay(player, vehicle.Price, bank, $"Remboursement {vehicle.Label}");
                Notify(player, "Erreur lors de la création du véhicule, vous avez été remboursé.", NotificationManager.Type.Error);
                return false;
            }

            if (vehicle.Stock > 0)
            {
                vehicle.Stock--;
                await vehicle.Save();
            }

            Notify(player, $"Félicitations ! Vous avez acheté {vehicle.Label} pour {FormatPrice(vehicle.Price)}. Il vous attend dans votre garage.", NotificationManager.Type.Success);
            Logger.LogSuccess(ConcessionnairePlugin.Title, $"{player.FullName} a acheté {vehicle.Label} (modèle {vehicle.ModelId}, véhicule #{vehicleId}) pour {vehicle.Price} € chez {PatternName}");
            return true;
        }

        private static void Pay(Player player, double amount, bool bank, string reason)
        {
            if (bank)
                player.AddBankMoney(amount);
            else
                player.AddMoney(amount, reason);
        }

        // ------------------------------------------------------------------
        //  Côté staff : gestion des concessionnaires, catalogues et points
        // ------------------------------------------------------------------

        public async Task SetProperties(int id)
        {
            DealershipPattern result = await Query(id);
            Id = id;
            TypeName = nameof(DealershipPattern);
            PatternName = result?.PatternName;
            AllowCash = result?.AllowCash ?? true;
            AllowBank = result?.AllowBank ?? true;
        }

        private DealershipPattern Prepare(DealershipPattern pattern)
        {
            pattern.TypeName = nameof(DealershipPattern);
            pattern.Context = Context;
            return pattern;
        }

        /// <summary>Menu principal staff : choisir un concessionnaire et le placer à sa position.</summary>
        public async void CreateOrGenerate(Player player)
        {
            if (!player.IsAdmin) return;

            List<DealershipPattern> patterns = await QueryAll();

            Panel panel = Context.PanelHelper.Create($"{Title} - Placer un point", UIPanel.PanelType.Tab, player, () => CreateOrGenerate(player));

            if (patterns.Count == 0)
            {
                panel.AddTabLine("Aucun concessionnaire, créez-en un", _ => { });
            }
            foreach (DealershipPattern pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}", async _ =>
                {
                    Prepare(pattern);
                    if (await Context.PointHelper.CreateNPoint(player, pattern))
                        Notify(player, $"Concessionnaire « {pattern.PatternName} » placé à votre position.", NotificationManager.Type.Success);
                    else
                        Notify(player, "Erreur lors de la création du point.", NotificationManager.Type.Error);
                });
            }

            if (patterns.Count > 0)
                panel.NextButton("Placer ici", () => panel.SelectTab());
            panel.NextButton("Nouveau", () => SetPatternData(player));
            panel.NextButton("Configurer", async () => await GetPatternData(player, true));
            panel.NextButton("Points", async () => await GetNPoints(player));
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Création d'un nouveau concessionnaire.</summary>
        public void SetPatternData(Player player)
        {
            Panel panel = Context.PanelHelper.Create($"{Title} - Nouveau", UIPanel.PanelType.Input, player, () => SetPatternData(player));
            panel.TextLines.Add("Nom du concessionnaire (affiché aux joueurs)");
            panel.SetInputPlaceholder("Concessionnaire de Amboise");

            panel.PreviousButtonWithAction("Créer", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    Notify(player, "Le nom ne peut pas être vide.", NotificationManager.Type.Error);
                    return false;
                }

                DealershipPattern pattern = new DealershipPattern(false) { PatternName = name };
                if (!await pattern.Save())
                {
                    Notify(player, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error);
                    return false;
                }

                Notify(player, $"Concessionnaire « {name} » créé. Ajoutez des véhicules via « Configurer ».", NotificationManager.Type.Success);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Liste des concessionnaires : configurer, renommer ou supprimer.</summary>
        public async Task GetPatternData(Player player, bool forEdit)
        {
            List<DealershipPattern> patterns = await QueryAll();
            string action = "";

            Panel panel = Context.PanelHelper.Create($"{Title} - Concessionnaires", UIPanel.PanelType.Tab, player, async () => await GetPatternData(player, forEdit));

            if (patterns.Count == 0)
            {
                panel.AddTabLine("Aucun concessionnaire", _ => { });
            }
            foreach (DealershipPattern pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}", async _ =>
                {
                    Prepare(pattern);
                    switch (action)
                    {
                        case "config":
                            pattern.ConfigureDealership(player);
                            break;
                        case "delete":
                            await Context.PointHelper.DeleteNPointsByPattern(player, pattern);
                            foreach (DealershipVehicle vehicle in await DealershipVehicle.Query(v => v.DealershipId == pattern.Id))
                                await vehicle.Delete();
                            if (await pattern.Delete())
                                Notify(player, $"Concessionnaire « {pattern.PatternName} », son catalogue et ses points supprimés.", NotificationManager.Type.Success);
                            else
                                Notify(player, "Erreur lors de la suppression.", NotificationManager.Type.Error);
                            panel.Refresh();
                            break;
                    }
                });
            }

            if (patterns.Count > 0 && forEdit)
            {
                panel.NextButton("Configurer", () => { action = "config"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Configuration d'un concessionnaire : catalogue, nom, paiements.</summary>
        public void ConfigureDealership(Player player)
        {
            Panel panel = Context.PanelHelper.Create($"{Title} - {PatternName}", UIPanel.PanelType.Tab, player, () => ConfigureDealership(player));

            panel.AddTabLine("Catalogue des véhicules", async _ => await ManageVehicles(player));
            panel.AddTabLine("Renommer", _ => RenamePattern(player));
            panel.AddTabLine($"Paiement en liquide : {(AllowCash ? Color("oui", Colors.Success) : Color("non", Colors.Error))}", async _ =>
            {
                AllowCash = !AllowCash;
                await Save();
                panel.Refresh();
            });
            panel.AddTabLine($"Paiement par banque : {(AllowBank ? Color("oui", Colors.Success) : Color("non", Colors.Error))}", async _ =>
            {
                AllowBank = !AllowBank;
                await Save();
                panel.Refresh();
            });

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void RenamePattern(Player player)
        {
            Panel panel = Context.PanelHelper.Create($"{Title} - Renommer", UIPanel.PanelType.Input, player, () => RenamePattern(player));
            panel.TextLines.Add($"Nouveau nom pour « {PatternName} »");
            panel.SetInputPlaceholder(PatternName ?? Title);

            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name)) return false;
                PatternName = name;
                return await Save();
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Catalogue staff : ajouter, modifier ou retirer des véhicules.</summary>
        private async Task ManageVehicles(Player player)
        {
            List<DealershipVehicle> vehicles = await DealershipVehicle.Query(v => v.DealershipId == Id);
            string action = "";

            Panel panel = Context.PanelHelper.Create($"{PatternName} - Catalogue", UIPanel.PanelType.Tab, player, async () => await ManageVehicles(player));

            if (vehicles.Count == 0)
            {
                panel.AddTabLine("Aucun véhicule, ajoutez-en un", _ => { });
            }
            foreach (DealershipVehicle vehicle in vehicles.OrderBy(v => v.Price))
            {
                panel.AddTabLine($"{vehicle.Label} (modèle {vehicle.ModelId}) - {FormatPrice(vehicle.Price)} - stock {FormatStock(vehicle.Stock)}", async _ =>
                {
                    if (action == "edit")
                    {
                        EditVehicle(player, vehicle);
                    }
                    else if (action == "delete")
                    {
                        if (await vehicle.Delete())
                            Notify(player, $"{vehicle.Label} retiré du catalogue.", NotificationManager.Type.Success);
                        panel.Refresh();
                    }
                });
            }

            panel.NextButton("Ajouter", () => AskVehicleField(player, new DealershipVehicle { DealershipId = Id }, 0, true));
            if (vehicles.Count > 0)
            {
                panel.NextButton("Modifier", () => { action = "edit"; panel.SelectTab(); });
                panel.AddButton(Color("Retirer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void EditVehicle(Player player, DealershipVehicle vehicle)
        {
            Panel panel = Context.PanelHelper.Create($"{PatternName} - {vehicle.Label}", UIPanel.PanelType.Tab, player, () => EditVehicle(player, vehicle));

            panel.AddTabLine($"Modèle : {vehicle.ModelId}", _ => AskVehicleField(player, vehicle, 0, false));
            panel.AddTabLine($"Nom : {vehicle.Label}", _ => AskVehicleField(player, vehicle, 1, false));
            panel.AddTabLine($"Prix : {FormatPrice(vehicle.Price)}", _ => AskVehicleField(player, vehicle, 2, false));
            panel.AddTabLine($"Stock : {FormatStock(vehicle.Stock)}", _ => AskVehicleField(player, vehicle, 3, false));

            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>
        /// Saisie d'un champ du véhicule (0 = modèle, 1 = nom, 2 = prix, 3 = stock).
        /// En création (<paramref name="wizard"/>), enchaîne les 4 champs puis enregistre.
        /// </summary>
        private void AskVehicleField(Player player, DealershipVehicle vehicle, int field, bool wizard)
        {
            string[] titles = { "Modèle", "Nom", "Prix", "Stock" };
            Panel panel = Context.PanelHelper.Create($"{PatternName} - {titles[field]}", UIPanel.PanelType.Input, player, () => AskVehicleField(player, vehicle, field, wizard));

            switch (field)
            {
                case 0:
                    panel.TextLines.Add("ID du modèle de véhicule Nova-Life (nombre)");
                    panel.SetInputPlaceholder(wizard ? "Ex. 1" : vehicle.ModelId.ToString());
                    break;
                case 1:
                    panel.TextLines.Add("Nom affiché aux joueurs");
                    panel.SetInputPlaceholder(wizard ? "Ex. Citadine" : vehicle.Label);
                    break;
                case 2:
                    panel.TextLines.Add("Prix de vente en €");
                    panel.SetInputPlaceholder(wizard ? "Ex. 15000" : vehicle.Price.ToString(CultureInfo.InvariantCulture));
                    break;
                case 3:
                    panel.TextLines.Add("Nombre d'exemplaires disponibles (-1 = illimité)");
                    panel.SetInputPlaceholder(wizard ? "-1" : vehicle.Stock.ToString());
                    break;
            }

            panel.AddButton(wizard && field < 3 ? "Suivant" : "Valider", async _ =>
            {
                string input = panel.inputText?.Trim();
                // En création, un stock vide signifie illimité
                if (wizard && field == 3 && string.IsNullOrEmpty(input)) input = "-1";

                if (!ApplyVehicleField(vehicle, field, input))
                {
                    Notify(player, "Valeur invalide.", NotificationManager.Type.Error);
                    return;
                }

                if (wizard && field < 3)
                {
                    AskVehicleField(player, vehicle, field + 1, true);
                    return;
                }

                if (!await vehicle.Save())
                {
                    Notify(player, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error);
                    return;
                }

                Notify(player, wizard ? $"{vehicle.Label} ajouté au catalogue." : $"{vehicle.Label} modifié.", NotificationManager.Type.Success);
                if (wizard)
                    await ManageVehicles(player);
                else
                    EditVehicle(player, vehicle);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static bool ApplyVehicleField(DealershipVehicle vehicle, int field, string input)
        {
            switch (field)
            {
                case 0:
                    if (!int.TryParse(input, out int modelId) || modelId < 0) return false;
                    vehicle.ModelId = modelId;
                    if (string.IsNullOrEmpty(vehicle.Label)) vehicle.Label = $"Véhicule #{modelId}";
                    return true;
                case 1:
                    if (string.IsNullOrEmpty(input)) return false;
                    vehicle.Label = input;
                    return true;
                case 2:
                    if (!TryParsePrice(input, out double price)) return false;
                    vehicle.Price = price;
                    return true;
                case 3:
                    if (!int.TryParse(input, out int stock) || stock < -1) return false;
                    vehicle.Stock = stock;
                    return true;
            }
            return false;
        }

        /// <summary>Liste de tous les points placés : se téléporter, déplacer ou supprimer.</summary>
        public async Task GetNPoints(Player player)
        {
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == nameof(DealershipPattern));
            Dictionary<int, string> names = (await QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);
            string action = "";

            Panel panel = Context.PanelHelper.Create($"{Title} - Points placés", UIPanel.PanelType.Tab, player, async () => await GetNPoints(player));

            if (points.Count == 0)
            {
                panel.AddTabLine("Aucun concessionnaire placé", _ => { });
            }
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
                                Notify(player, "Point déplacé à votre position.", NotificationManager.Type.Success);
                            break;
                        case "delete":
                            await Context.PointHelper.DeleteNPoint(point);
                            Notify(player, $"Point #{point.Id} supprimé.", NotificationManager.Type.Success);
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
