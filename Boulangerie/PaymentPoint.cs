using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.Network;
using Life.UI;
using ModKit.Helper;
using ModKit.Helper.PointHelper;
using ModKit.ORM;
using SQLite;
using static ModKit.Helper.TextFormattingHelper;

namespace Boulangerie
{
    /// <summary>
    /// Point de paiement côté client (point bleu), lié à une entreprise. Le boulanger y envoie
    /// la commande depuis sa caisse ; le client marche dans le point, voit le récapitulatif
    /// et paie en espèces ou par carte.
    /// </summary>
    public class PaymentPoint : ModEntity<PaymentPoint>, PatternData
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PatternName { get; set; }

        public int BizId { get; set; }

        [Ignore]
        public string TypeName { get; set; } = nameof(PaymentPoint);

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        private const string Tag = BoulangeriePlugin.Tag;

        private static BoulangeriePlugin Plugin => BoulangeriePlugin.Instance;

        public PaymentPoint() { }

        public PaymentPoint(bool isCreated)
        {
            TypeName = nameof(PaymentPoint);
        }

        // ------------------------------------------------------------------
        //  Côté client
        // ------------------------------------------------------------------

        public void OnPlayerTrigger(Player player)
        {
            int bizId = BizId;
            string name = string.IsNullOrWhiteSpace(PatternName) ? "Paiement" : PatternName;

            List<PaymentRequest> orders = CashRegister.ActiveRequests()
                .Where(r => r.AtPoint && r.Cart.BizId == bizId && (r.ClientNetId == player.netId || r.ClientNetId == 0))
                .OrderBy(r => r.OrderNumber)
                .ToList();

            // Sa propre commande passe en premier
            PaymentRequest mine = orders.FirstOrDefault(r => r.ClientNetId == player.netId);
            if (mine != null)
            {
                CashRegister.ShowPaymentPanel(player, mine);
                return;
            }
            if (orders.Count == 0)
            {
                player.Notify(Tag, "Aucune commande à payer ici. Passez d'abord commande au comptoir.", NotificationManager.Type.Info, 5f);
                return;
            }
            if (orders.Count == 1)
            {
                CashRegister.ShowPaymentPanel(player, orders[0]);
                return;
            }
            ChooseOrder(player, name, orders);
        }

        /// <summary>Plusieurs commandes au comptoir en attente : le client choisit la sienne.</summary>
        private static void ChooseOrder(Player player, string name, List<PaymentRequest> orders)
        {
            Panel panel = Plugin.PanelHelper.Create($"{name} - Commandes", UIPanel.PanelType.TabPrice, player, () => ChooseOrder(player, name, orders));
            foreach (PaymentRequest order in orders)
            {
                PaymentRequest o = order;
                string content = string.Join(", ", o.Cart.Lines.Select(l => $"{l.Quantity}x {l.Name}"));
                panel.AddTabLine($"Commande n°{o.OrderNumber} - servie par {o.CashierName}<br>" + Size(Color(content, Colors.Grey), 14),
                    BoulangeriePlugin.Money(o.Cart.Total), ModKit.Utils.IconUtils.Others.None.Id, _ =>
                    {
                        if (o.Done || o.Expired)
                        {
                            player.Notify(Tag, "Cette commande n'est plus disponible.", NotificationManager.Type.Warning, 5f);
                            return;
                        }
                        panel.Close();
                        CashRegister.ShowPaymentPanel(player, o);
                    });
            }
            panel.AddButton("Voir / payer", _ => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        // ------------------------------------------------------------------
        //  Côté staff : modèles et points (AAMenu > Points)
        // ------------------------------------------------------------------

        public async Task SetProperties(int id)
        {
            PaymentPoint result = await Query(id);
            Id = id;
            TypeName = nameof(PaymentPoint);
            PatternName = result?.PatternName;
            BizId = result?.BizId ?? 0;
        }

        public async void CreateOrGenerate(Player player)
        {
            if (!BoulangeriePlugin.IsStaff(player))
            {
                player.Notify(Tag, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
                return;
            }

            List<PaymentPoint> patterns = await QueryAll();
            Panel panel = Context.PanelHelper.Create("Paiement - Placer un point", UIPanel.PanelType.Tab, player, () => CreateOrGenerate(player));

            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle, créez-en un", _ => panel.Refresh());
            foreach (PaymentPoint pattern in patterns)
            {
                PaymentPoint p = pattern;
                panel.AddTabLine($"[{p.Id}] {p.PatternName} - {BoulangeriePlugin.BizName(p.BizId)}", async _ =>
                {
                    p.TypeName = nameof(PaymentPoint);
                    p.Context = Context;
                    if (await Context.PointHelper.CreateNPoint(player, p))
                        player.Notify(Tag, $"Point de paiement « {p.PatternName} » placé à votre position.", NotificationManager.Type.Success, 5f);
                    else
                        player.Notify(Tag, "Erreur lors de la création du point.", NotificationManager.Type.Error, 5f);
                });
            }

            if (patterns.Count > 0)
                panel.AddButton("Placer ici", _ => panel.SelectTab());
            panel.NextButton("Nouveau modèle", () => SetPatternData(player));
            panel.NextButton("Modèles", async () => await GetPatternData(player, true));
            panel.NextButton("Points", async () => await GetNPoints(player));
            panel.CloseButton();
            panel.Display();
        }

        public void SetPatternData(Player player)
        {
            const string title = "Paiement - Nouveau modèle";
            Panel panel = Context.PanelHelper.Create(title, UIPanel.PanelType.Input, player, () => SetPatternData(player));
            panel.TextLines.Add("Étape 1/2 : nom du point de paiement (ex. « Paiement client »)");
            panel.SetInputPlaceholder("Paiement client");

            panel.AddButton("Suivant", _ =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    player.Notify(Tag, "Le nom ne peut pas être vide.", NotificationManager.Type.Error, 5f);
                    return;
                }
                player.ClosePanel(panel);
                Plugin.ChooseBiz(player, title, null, async (bizPanel, biz) =>
                {
                    PaymentPoint pattern = new PaymentPoint(false) { PatternName = name, BizId = biz.Id };
                    if (await pattern.Save())
                        player.Notify(Tag, $"Point « {name} » créé pour {BoulangeriePlugin.BizName(biz.Id)}. Choisissez-le puis « Placer ici ».", NotificationManager.Type.Success, 6f);
                    else
                        player.Notify(Tag, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
                    bizPanel.Previous();
                });
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public async Task GetPatternData(Player player, bool forEdit)
        {
            List<PaymentPoint> patterns = await QueryAll();
            string action = "";

            Panel panel = Context.PanelHelper.Create("Paiement - Modèles", UIPanel.PanelType.Tab, player, async () => await GetPatternData(player, forEdit));

            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle", _ => panel.Refresh());
            foreach (PaymentPoint pattern in patterns)
            {
                PaymentPoint p = pattern;
                panel.AddTabLine($"[{p.Id}] {p.PatternName} - {BoulangeriePlugin.BizName(p.BizId)}", async _ =>
                {
                    p.TypeName = nameof(PaymentPoint);
                    p.Context = Context;
                    switch (action)
                    {
                        case "rename":
                            Plugin.AskInput(player, "Paiement - Modifier", $"Nouveau nom pour « {p.PatternName} »", p.PatternName, async input =>
                            {
                                if (input.Length == 0) return false;
                                p.PatternName = input;
                                return await p.Save();
                            });
                            break;
                        case "biz":
                            Plugin.ChooseBiz(player, "Paiement - Entreprise", null, async (bizPanel, biz) =>
                            {
                                p.BizId = biz.Id;
                                await p.Save();
                                player.Notify(Tag, $"« {p.PatternName} » appartient maintenant à {BoulangeriePlugin.BizName(biz.Id)}.", NotificationManager.Type.Success, 5f);
                                bizPanel.Previous();
                            });
                            break;
                        case "delete":
                            await Context.PointHelper.DeleteNPointsByPattern(player, p);
                            if (await p.Delete())
                                player.Notify(Tag, $"Modèle « {p.PatternName} » et ses points supprimés.", NotificationManager.Type.Success, 5f);
                            else
                                player.Notify(Tag, "Erreur lors de la suppression.", NotificationManager.Type.Error, 5f);
                            panel.Refresh();
                            break;
                        default:
                            panel.Refresh();
                            break;
                    }
                });
            }

            if (patterns.Count > 0 && forEdit)
            {
                panel.NextButton("Renommer", () => { action = "rename"; panel.SelectTab(); });
                panel.NextButton("Entreprise", () => { action = "biz"; panel.SelectTab(); });
                panel.NextButton(Color("Supprimer", Colors.Error), () => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public async Task GetNPoints(Player player)
        {
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == nameof(PaymentPoint));
            Dictionary<int, string> names = (await QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);
            string action = "";

            Panel panel = Context.PanelHelper.Create("Paiement - Points placés", UIPanel.PanelType.Tab, player, async () => await GetNPoints(player));

            if (points.Count == 0)
                panel.AddTabLine("Aucun point de paiement placé", _ => { });
            foreach (NPoint point in points)
            {
                NPoint pt = point;
                string name = names.TryGetValue(pt.PatternId, out string n) ? n : "?";
                panel.AddTabLine($"Point #{pt.Id} - {name}", async _ =>
                {
                    switch (action)
                    {
                        case "tp":
                            Context.PointHelper.PlayerSetPositionToNPoint(player, pt);
                            break;
                        case "move":
                            if (await Context.PointHelper.SetNPointPosition(player, pt))
                                player.Notify(Tag, "Point déplacé à votre position.", NotificationManager.Type.Success, 5f);
                            break;
                        case "delete":
                            await Context.PointHelper.DeleteNPoint(pt);
                            player.Notify(Tag, $"Point #{pt.Id} supprimé.", NotificationManager.Type.Success, 5f);
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
