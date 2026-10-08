using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.DB;
using Life.Network;
using Life.PermissionSystem;
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
    /// Concessionnaire by Loris Strange.
    /// Le staff crée autant de concessions qu'il veut (chacune avec son propre catalogue
    /// de véhicules et ses prix), puis place un ou plusieurs points bleus par concession.
    /// Les joueurs entrent dans un point, choisissent un véhicule et l'achètent :
    /// le véhicule leur appartient et apparaît dans leur garage.
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

            DealershipPattern pattern = new DealershipPattern(false);
            PointHelper.AddPattern(nameof(DealershipPattern), pattern);
            if (AAMenu.AAMenu.menu != null)
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, Title, pattern, this);
            else
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : utilisez la commande /concession.");

            RegisterCommands();

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version} by Loris Strange", "initialisé");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            PointHelper.InitAllNPoint(player);
        }

        private void RegisterCommands()
        {
            SChatCommand command = new SChatCommand("/concession", new string[] { "/concess" }, "Gérer les concessionnaires (staff)", "/concession",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!DealershipPattern.IsStaff(player))
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
    /// <summary>Un véhicule en vente dans une concession.</summary>
    public class DealershipVehicle : ModEntity<DealershipVehicle>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        /// <summary>Id de la concession (DealershipPattern) qui vend ce véhicule.</summary>
        public int DealershipId { get; set; }

        /// <summary>Id du modèle de véhicule dans le jeu.</summary>
        public int ModelId { get; set; }

        /// <summary>Nom affiché aux joueurs.</summary>
        public string Name { get; set; }

        public double Price { get; set; }

        public DealershipVehicle() { }
    }
}

namespace Concessionnaire
{
    /// <summary>
    /// Une concession. Chaque concession a son nom et son catalogue, et peut être
    /// placée à plusieurs endroits (un NPoint par placement).
    /// </summary>
    public class DealershipPattern : ModEntity<DealershipPattern>, PatternData
    {
        private const string Title = ConcessionnairePlugin.Title;

        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PatternName { get; set; }

        [Ignore]
        public string TypeName { get; set; } = nameof(DealershipPattern);

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        public DealershipPattern() { }

        public DealershipPattern(bool isCreated)
        {
            TypeName = nameof(DealershipPattern);
        }

        public static bool IsStaff(Player player) => player.IsAdmin && player.serviceAdmin;

        private static string FormatPrice(double price) => $"{price:N0}€";

        private static Task<List<DealershipVehicle>> GetCatalog(int dealershipId)
            => DealershipVehicle.Query(v => v.DealershipId == dealershipId);

        // ------------------------------------------------------------------
        //  Côté joueur : catalogue et achat
        // ------------------------------------------------------------------

        public void OnPlayerTrigger(Player player)
        {
            OpenCatalog(player);
        }

        private async void OpenCatalog(Player player)
        {
            List<DealershipVehicle> vehicles = (await GetCatalog(Id)).OrderBy(v => v.Price).ToList();

            Panel panel = Context.PanelHelper.Create(PatternName ?? Title, UIPanel.PanelType.Tab, player, () => OpenCatalog(player));

            if (vehicles.Count == 0)
                panel.AddTabLine("Aucun véhicule en vente pour le moment", _ => { });

            foreach (DealershipVehicle vehicle in vehicles)
            {
                panel.AddTabLine($"{vehicle.Name} - {Color(FormatPrice(vehicle.Price), Colors.Success)}", _ => ConfirmPurchase(player, vehicle));
            }

            if (vehicles.Count > 0)
                panel.NextButton("Acheter", () => panel.SelectTab());
            if (IsStaff(player))
                panel.NextButton(Color("Gérer le catalogue", Colors.Warning), async () => await ManageCatalog(player, this));
            panel.CloseButton();
            panel.Display();
        }

        private void ConfirmPurchase(Player player, DealershipVehicle vehicle)
        {
            Panel panel = Context.PanelHelper.Create($"Acheter : {vehicle.Name}", UIPanel.PanelType.Text, player, () => ConfirmPurchase(player, vehicle));
            panel.TextLines.Add($"Véhicule : {vehicle.Name}");
            panel.TextLines.Add($"Prix : {Color(FormatPrice(vehicle.Price), Colors.Success)}");
            panel.TextLines.Add($"Votre argent liquide : {FormatPrice(player.character.Money)}");
            panel.TextLines.Add("Le véhicule sera livré dans votre garage.");

            panel.PreviousButtonWithAction("Confirmer l'achat", async () => await Buy(player, vehicle));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private async Task<bool> Buy(Player player, DealershipVehicle vehicle)
        {
            // Le prix est relu en base au cas où le staff l'a modifié entre-temps
            DealershipVehicle current = await DealershipVehicle.Query(vehicle.Id);
            if (current == null || current.DealershipId != Id)
            {
                player.Notify(Title, "Ce véhicule n'est plus en vente.", NotificationManager.Type.Error, 5f);
                return false;
            }

            if (player.character.Money < current.Price)
            {
                player.Notify(Title, $"Il vous manque {FormatPrice(current.Price - player.character.Money)}.", NotificationManager.Type.Error, 5f);
                return false;
            }

            // On encaisse avant la création pour éviter un double achat, et on rembourse en cas d'échec
            player.AddMoney(-current.Price, $"Achat véhicule {current.Name} ({PatternName})");

            Permissions permissions = new Permissions
            {
                owner = new Entity { characterId = player.character.Id },
                coOwners = new List<Entity>()
            };

            object created = null; // LifeVehicle renvoyé par LifeDB
            try
            {
                created = await LifeDB.CreateVehicle(current.ModelId, JsonConvert.SerializeObject(permissions));
            }
            catch (Exception e)
            {
                Logger.LogError(Title, $"Création du véhicule {current.ModelId} impossible : {e.Message}");
            }

            if (created == null)
            {
                player.AddMoney(current.Price, $"Remboursement véhicule {current.Name}");
                player.Notify(Title, "Erreur lors de la création du véhicule, vous avez été remboursé.", NotificationManager.Type.Error, 5f);
                return false;
            }

            player.Notify(Title, $"Félicitations ! Vous avez acheté {current.Name} pour {FormatPrice(current.Price)}. Il vous attend au garage.", NotificationManager.Type.Success, 8f);
            return true;
        }

        // ------------------------------------------------------------------
        //  Côté staff : concessions, catalogues et points (AAMenu / /concession)
        // ------------------------------------------------------------------

        public async Task SetProperties(int id)
        {
            DealershipPattern result = await Query(id);
            Id = id;
            TypeName = nameof(DealershipPattern);
            PatternName = result?.PatternName;
        }

        /// <summary>Menu principal staff : choisir une concession et la placer à sa position.</summary>
        public async void CreateOrGenerate(Player player)
        {
            if (!player.IsAdmin) return;

            List<DealershipPattern> patterns = await QueryAll();

            Panel panel = Context.PanelHelper.Create($"{Title} - Placer une concession", UIPanel.PanelType.Tab, player, () => CreateOrGenerate(player));

            if (patterns.Count == 0)
                panel.AddTabLine("Aucune concession, créez-en une", _ => { });

            foreach (DealershipPattern pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}", async _ =>
                {
                    Prepare(pattern);
                    if (await Context.PointHelper.CreateNPoint(player, pattern))
                        player.Notify(Title, $"Concession « {pattern.PatternName} » placée à votre position.", NotificationManager.Type.Success, 5f);
                    else
                        player.Notify(Title, "Erreur lors de la création du point.", NotificationManager.Type.Error, 5f);
                });
            }

            if (patterns.Count > 0)
                panel.NextButton("Placer ici", () => panel.SelectTab());
            panel.NextButton("Nouvelle concession", () => SetPatternData(player));
            panel.NextButton("Concessions", async () => await GetPatternData(player, true));
            panel.NextButton("Points", async () => await GetNPoints(player));
            panel.CloseButton();
            panel.Display();
        }

        private void Prepare(DealershipPattern pattern)
        {
            pattern.TypeName = nameof(DealershipPattern);
            pattern.Context = Context;
        }

        /// <summary>Création d'une nouvelle concession.</summary>
        public void SetPatternData(Player player)
        {
            Panel panel = Context.PanelHelper.Create($"{Title} - Nouvelle concession", UIPanel.PanelType.Input, player, () => SetPatternData(player));
            panel.TextLines.Add("Nom de la concession (affiché aux joueurs)");
            panel.SetInputPlaceholder("Concession du centre-ville");

            panel.PreviousButtonWithAction("Créer", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    player.Notify(Title, "Le nom ne peut pas être vide.", NotificationManager.Type.Error, 5f);
                    return false;
                }

                DealershipPattern pattern = new DealershipPattern(false) { PatternName = name };
                if (!await pattern.Save())
                {
                    player.Notify(Title, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
                    return false;
                }

                player.Notify(Title, $"Concession « {name} » créée. Ajoutez des véhicules via « Concessions ».", NotificationManager.Type.Success, 5f);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Liste des concessions : gérer le catalogue, renommer ou supprimer.</summary>
        public async Task GetPatternData(Player player, bool forEdit)
        {
            List<DealershipPattern> patterns = await QueryAll();
            string action = "";

            Panel panel = Context.PanelHelper.Create($"{Title} - Concessions", UIPanel.PanelType.Tab, player, async () => await GetPatternData(player, forEdit));

            if (patterns.Count == 0)
                panel.AddTabLine("Aucune concession", _ => { });

            foreach (DealershipPattern pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}", async _ =>
                {
                    Prepare(pattern);
                    switch (action)
                    {
                        case "catalog":
                            await ManageCatalog(player, pattern);
                            break;
                        case "rename":
                            RenamePattern(player, pattern);
                            break;
                        case "delete":
                            await Context.PointHelper.DeleteNPointsByPattern(player, pattern);
                            foreach (DealershipVehicle vehicle in await GetCatalog(pattern.Id))
                                await vehicle.Delete();
                            if (await pattern.Delete())
                                player.Notify(Title, $"Concession « {pattern.PatternName} » supprimée (catalogue et points compris).", NotificationManager.Type.Success, 5f);
                            else
                                player.Notify(Title, "Erreur lors de la suppression.", NotificationManager.Type.Error, 5f);
                            panel.Refresh();
                            break;
                    }
                });
            }

            if (patterns.Count > 0 && forEdit)
            {
                panel.NextButton("Catalogue", () => { action = "catalog"; panel.SelectTab(); });
                panel.NextButton("Renommer", () => { action = "rename"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void RenamePattern(Player player, DealershipPattern pattern)
        {
            Panel panel = Context.PanelHelper.Create($"{Title} - Renommer", UIPanel.PanelType.Input, player, () => RenamePattern(player, pattern));
            panel.TextLines.Add($"Nouveau nom pour « {pattern.PatternName} »");
            panel.SetInputPlaceholder(pattern.PatternName ?? Title);

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

        /// <summary>Catalogue d'une concession : ajouter, modifier ou retirer des véhicules.</summary>
        private async Task ManageCatalog(Player player, DealershipPattern pattern)
        {
            if (!IsStaff(player)) return;

            List<DealershipVehicle> vehicles = (await GetCatalog(pattern.Id)).OrderBy(v => v.Price).ToList();
            string action = "";

            Panel panel = Context.PanelHelper.Create($"Catalogue - {pattern.PatternName}", UIPanel.PanelType.Tab, player, async () => await ManageCatalog(player, pattern));

            if (vehicles.Count == 0)
                panel.AddTabLine("Aucun véhicule, ajoutez-en un", _ => { });

            foreach (DealershipVehicle vehicle in vehicles)
            {
                panel.AddTabLine($"{vehicle.Name} (modèle {vehicle.ModelId}) - {FormatPrice(vehicle.Price)}", async _ =>
                {
                    if (action == "edit")
                    {
                        EditVehicle(player, vehicle);
                    }
                    else if (action == "delete")
                    {
                        if (await vehicle.Delete())
                            player.Notify(Title, $"{vehicle.Name} retiré du catalogue.", NotificationManager.Type.Success, 5f);
                        panel.Refresh();
                    }
                });
            }

            panel.NextButton("Ajouter", () => EditVehicle(player, new DealershipVehicle { DealershipId = pattern.Id, ModelId = -1 }));
            if (vehicles.Count > 0)
            {
                panel.NextButton("Modifier", () => { action = "edit"; panel.SelectTab(); });
                panel.AddButton(Color("Retirer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Fiche d'un véhicule : nom, modèle et prix, chacun modifiable.</summary>
        private void EditVehicle(Player player, DealershipVehicle vehicle)
        {
            bool isNew = vehicle.Id == 0;
            Panel panel = Context.PanelHelper.Create(isNew ? "Nouveau véhicule" : $"Modifier : {vehicle.Name}", UIPanel.PanelType.Tab, player, () => EditVehicle(player, vehicle));

            panel.AddTabLine($"Nom : {vehicle.Name ?? Color("à définir", Colors.Error)}",
                _ => AskValue(player, vehicle, "Nom affiché aux joueurs", vehicle.Name ?? "Citadine", input =>
                {
                    vehicle.Name = input;
                    return true;
                }));
            panel.AddTabLine($"Id du modèle : {(vehicle.ModelId >= 0 ? vehicle.ModelId.ToString() : Color("à définir", Colors.Error))}",
                _ => AskValue(player, vehicle, "Id du modèle de véhicule (voir README)", vehicle.ModelId >= 0 ? vehicle.ModelId.ToString() : "0", input =>
                {
                    if (!int.TryParse(input, out int modelId) || modelId < 0) return false;
                    vehicle.ModelId = modelId;
                    return true;
                }));
            panel.AddTabLine($"Prix : {FormatPrice(vehicle.Price)}",
                _ => AskValue(player, vehicle, "Prix de vente en €", vehicle.Price.ToString("0"), input =>
                {
                    if (!double.TryParse(input, out double price) || price < 0) return false;
                    vehicle.Price = price;
                    return true;
                }));

            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.PreviousButtonWithAction(Color("Enregistrer", Colors.Success), async () =>
            {
                if (string.IsNullOrEmpty(vehicle.Name) || vehicle.ModelId < 0)
                {
                    player.Notify(Title, "Renseignez au moins le nom et l'id du modèle.", NotificationManager.Type.Error, 5f);
                    return false;
                }
                if (!await vehicle.Save())
                {
                    player.Notify(Title, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
                    return false;
                }
                player.Notify(Title, $"{vehicle.Name} enregistré ({FormatPrice(vehicle.Price)}).", NotificationManager.Type.Success, 5f);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Saisie d'une valeur ; revient à la fiche du véhicule si elle est valide.</summary>
        private void AskValue(Player player, DealershipVehicle vehicle, string label, string placeholder, Func<string, bool> apply)
        {
            Panel panel = Context.PanelHelper.Create(label, UIPanel.PanelType.Input, player, () => AskValue(player, vehicle, label, placeholder, apply));
            panel.TextLines.Add(label);
            panel.SetInputPlaceholder(placeholder);

            panel.PreviousButtonWithAction("Valider", () =>
            {
                string input = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(input) || !apply(input))
                {
                    player.Notify(Title, "Valeur invalide.", NotificationManager.Type.Error, 5f);
                    return Task.FromResult(false);
                }
                return Task.FromResult(true);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        /// <summary>Liste de tous les points de concession placés : se téléporter, déplacer ou supprimer.</summary>
        public async Task GetNPoints(Player player)
        {
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == nameof(DealershipPattern));
            Dictionary<int, string> names = (await QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);
            string action = "";

            Panel panel = Context.PanelHelper.Create($"{Title} - Points placés", UIPanel.PanelType.Tab, player, async () => await GetNPoints(player));

            if (points.Count == 0)
                panel.AddTabLine("Aucune concession placée", _ => { });

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
                                player.Notify(Title, "Point déplacé à votre position.", NotificationManager.Type.Success, 5f);
                            break;
                        case "delete":
                            await Context.PointHelper.DeleteNPoint(point);
                            player.Notify(Title, $"Point #{point.Id} supprimé.", NotificationManager.Type.Success, 5f);
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
