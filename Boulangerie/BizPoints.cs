using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Life;
using Life.Network;
using Life.UI;
using ModKit.Helper;
using ModKit.Helper.PointHelper;
using ModKit.ORM;
using ModKit.Utils;
using SQLite;
using static ModKit.Helper.TextFormattingHelper;

namespace Boulangerie
{
    /// <summary>Point bleu lié à une entreprise (ex. la carte client).</summary>
    public interface IBizPoint : PatternData
    {
        int BizId { get; set; }
    }

    /// <summary>
    /// Menus staff communs aux points liés à une entreprise : placer, créer un modèle
    /// (nom + entreprise), renommer / changer d'entreprise / supprimer, gérer les points placés.
    /// </summary>
    public static class BizPointAdmin<T> where T : ModEntity<T>, IBizPoint, new()
    {
        private const string Tag = BoulangeriePlugin.Tag;

        private static string TypeName => typeof(T).Name;

        public static async void CreateOrGenerate(T self, Player player, string label, string example)
        {
            if (!BoulangeriePlugin.IsStaff(player))
            {
                player.Notify(Tag, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
                return;
            }
            ModKit.ModKit ctx = self.Context ?? BoulangeriePlugin.Instance;

            List<T> patterns = await ModEntity<T>.QueryAll();
            Panel panel = ctx.PanelHelper.Create(BoulangeriePlugin.Brand($"{label} - Placer un point"), UIPanel.PanelType.Tab, player, () => CreateOrGenerate(self, player, label, example));

            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle, créez-en un", _ => panel.Refresh());
            foreach (T pattern in patterns)
            {
                T p = pattern;
                panel.AddTabLine($"[{p.Id}] {p.PatternName} - {BoulangeriePlugin.BizName(p.BizId)}", async _ =>
                {
                    p.TypeName = TypeName;
                    p.Context = ctx;
                    if (await ctx.PointHelper.CreateNPoint(player, p))
                        player.Notify(Tag, $"« {p.PatternName} » placé à votre position.", NotificationManager.Type.Success, 5f);
                    else
                        player.Notify(Tag, "Erreur lors de la création du point.", NotificationManager.Type.Error, 5f);
                });
            }

            if (patterns.Count > 0)
                panel.AddButton("Placer ici", _ => panel.SelectTab());
            panel.NextButton("Nouveau modèle", () => NewPattern(ctx, player, label, example));
            panel.NextButton("Modèles", () => Patterns(ctx, player, label));
            panel.NextButton("Points", () => Points(ctx, player, label));
            panel.CloseButton();
            panel.Display();
        }

        public static void NewPattern(ModKit.ModKit ctx, Player player, string label, string example)
        {
            string title = $"{label} - Nouveau modèle";
            Panel panel = ctx.PanelHelper.Create(BoulangeriePlugin.Brand(title), UIPanel.PanelType.Input, player, () => NewPattern(ctx, player, label, example));
            panel.TextLines.Add($"Étape 1/2 : nom du point (ex. « {example} »)");
            panel.SetInputPlaceholder(example);

            panel.AddButton("Suivant", _ =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    player.Notify(Tag, "Le nom ne peut pas être vide.", NotificationManager.Type.Error, 5f);
                    return;
                }
                player.ClosePanel(panel);
                BoulangeriePlugin.Instance.ChooseBiz(player, title, null, async (bizPanel, biz) =>
                {
                    T pattern = new T { PatternName = name, BizId = biz.Id, TypeName = TypeName };
                    if (await pattern.Save())
                        player.Notify(Tag, $"« {name} » créé pour {BoulangeriePlugin.BizName(biz.Id)}. Choisissez-le puis « Placer ici ».", NotificationManager.Type.Success, 6f);
                    else
                        player.Notify(Tag, "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
                    bizPanel.Previous();
                });
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public static async void Patterns(ModKit.ModKit ctx, Player player, string label)
        {
            List<T> patterns = await ModEntity<T>.QueryAll();
            string action = "";

            Panel panel = ctx.PanelHelper.Create(BoulangeriePlugin.Brand($"{label} - Modèles"), UIPanel.PanelType.Tab, player, () => Patterns(ctx, player, label));

            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle", _ => panel.Refresh());
            foreach (T pattern in patterns)
            {
                T p = pattern;
                panel.AddTabLine($"[{p.Id}] {p.PatternName} - {BoulangeriePlugin.BizName(p.BizId)}", async _ =>
                {
                    p.TypeName = TypeName;
                    p.Context = ctx;
                    switch (action)
                    {
                        case "rename":
                            BoulangeriePlugin.Instance.AskInput(player, $"{label} - Modifier", $"Nouveau nom pour « {p.PatternName} »", p.PatternName, async input =>
                            {
                                if (input.Length == 0) return false;
                                p.PatternName = input;
                                return await p.Save();
                            });
                            break;
                        case "biz":
                            BoulangeriePlugin.Instance.ChooseBiz(player, $"{label} - Entreprise", null, async (bizPanel, biz) =>
                            {
                                p.BizId = biz.Id;
                                await p.Save();
                                player.Notify(Tag, $"« {p.PatternName} » appartient maintenant à {BoulangeriePlugin.BizName(biz.Id)}.", NotificationManager.Type.Success, 5f);
                                bizPanel.Previous();
                            });
                            break;
                        case "delete":
                            await ctx.PointHelper.DeleteNPointsByPattern(player, p);
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

            if (patterns.Count > 0)
            {
                panel.NextButton("Renommer", () => { action = "rename"; panel.SelectTab(); });
                panel.NextButton("Entreprise", () => { action = "biz"; panel.SelectTab(); });
                panel.NextButton(Color("Supprimer", Colors.Error), () => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public static async void Points(ModKit.ModKit ctx, Player player, string label)
        {
            string typeName = TypeName;
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == typeName);
            Dictionary<int, string> names = (await ModEntity<T>.QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);
            string action = "";

            Panel panel = ctx.PanelHelper.Create(BoulangeriePlugin.Brand($"{label} - Points placés"), UIPanel.PanelType.Tab, player, () => Points(ctx, player, label));

            if (points.Count == 0)
                panel.AddTabLine("Aucun point placé", _ => { });
            foreach (NPoint point in points)
            {
                NPoint pt = point;
                string name = names.TryGetValue(pt.PatternId, out string n) ? n : "?";
                panel.AddTabLine($"Point #{pt.Id} - {name}", async _ =>
                {
                    switch (action)
                    {
                        case "tp":
                            ctx.PointHelper.PlayerSetPositionToNPoint(player, pt);
                            break;
                        case "move":
                            if (await ctx.PointHelper.SetNPointPosition(player, pt))
                                player.Notify(Tag, "Point déplacé à votre position.", NotificationManager.Type.Success, 5f);
                            break;
                        case "delete":
                            await ctx.PointHelper.DeleteNPoint(pt);
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

    /// <summary>Point bleu « Carte » : les clients y consultent la carte et y paient leur commande.</summary>
    public class MenuBoard : ModEntity<MenuBoard>, IBizPoint
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PatternName { get; set; }

        public int BizId { get; set; }

        [Ignore]
        public string TypeName { get; set; } = nameof(MenuBoard);

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        public const string Label = "Carte";


        public MenuBoard() { }

        public MenuBoard(bool isCreated)
        {
            TypeName = nameof(MenuBoard);
        }

        public void OnPlayerTrigger(Player player) => MenuCard.Show(player, BizId, false);

        public async Task SetProperties(int id)
        {
            MenuBoard result = await Query(id);
            Id = id;
            TypeName = nameof(MenuBoard);
            PatternName = result?.PatternName;
            BizId = result?.BizId ?? 0;
        }

        public void CreateOrGenerate(Player player) => BizPointAdmin<MenuBoard>.CreateOrGenerate(this, player, Label, "Carte de la boulangerie");

        public void SetPatternData(Player player) => BizPointAdmin<MenuBoard>.NewPattern(Context, player, Label, "Carte de la boulangerie");

        public Task GetPatternData(Player player, bool forEdit)
        {
            BizPointAdmin<MenuBoard>.Patterns(Context, player, Label);
            return Task.CompletedTask;
        }

        public Task GetNPoints(Player player)
        {
            BizPointAdmin<MenuBoard>.Points(Context, player, Label);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Panel du point « Carte » : en haut la commande du client à payer (récapitulatif +
    /// espèces / carte bancaire), en dessous la carte de la boulangerie (produits, stock, formules).
    /// </summary>
    public static class MenuCard
    {
        private const string Tag = BoulangeriePlugin.Tag;

        /// <param name="withBack">Vrai si ouverte depuis un autre menu (bouton « Retour », pas de paiement).</param>
        /// <param name="selected">Commande au comptoir choisie par le client parmi plusieurs.</param>
        public static async void Show(Player player, int bizId, bool withBack, PaymentRequest selected = null)
        {
            var (products, formulas) = await StockHelper.Load(bizId);
            Dictionary<int, string> names = products.ToDictionary(p => p.Key, p => p.Value.Name);
            int none = BoulangeriePlugin.DefaultIcon;
            string Money(double v) => BoulangeriePlugin.Money(v);

            Panel panel = BoulangeriePlugin.Instance.PanelHelper.Create(BoulangeriePlugin.Brand($"{BoulangeriePlugin.BizName(bizId)} - La carte"), UIPanel.PanelType.TabPrice, player,
                () => Show(player, bizId, withBack, selected));

            // ---- Commande à payer (seulement pour le client au point Carte) ----
            if (!withBack)
            {
                List<PaymentRequest> orders = CashRegister.ActiveRequests()
                    .Where(r => r.AtPoint && r.Cart.BizId == bizId && (r.ClientNetId == player.netId || r.ClientNetId == 0))
                    .OrderBy(r => r.OrderNumber)
                    .ToList();
                PaymentRequest current = orders.FirstOrDefault(r => r.ClientNetId == player.netId)
                    ?? (selected != null && orders.Contains(selected) ? selected : null)
                    ?? (orders.Count == 1 ? orders[0] : null);

                if (current != null)
                {
                    PaymentRequest req = current;
                    Cart c = req.Cart;
                    req.ClientPanel = panel;
                    panel.AddTabLine(Bold(Color($"VOTRE COMMANDE n°{req.OrderNumber}", Colors.Success)), Color($"servie par {req.CashierName}", Colors.Grey), none, _ => panel.Refresh());
                    foreach (CartLine line in c.Lines)
                        panel.AddTabLine($"{line.Quantity}x {(line.IsFormula ? "Formule " : "")}{line.Name}", Money(line.Total), none, _ => panel.Refresh());
                    if (c.DiscountPercent > 0)
                        panel.AddTabLine(Color($"Remise {c.DiscountPercent}%", Colors.Success), "-" + Money(c.Discount), none, _ => panel.Refresh());
                    panel.AddTabLine(Size(Bold("TOTAL À PAYER"), 20) + "<br>" + Size(Color($"Espèces : {Money(player.Money)} | Banque : {Money(player.Bank)}", Colors.Grey), 14),
                        Bold(Money(c.Total)), none, _ => panel.Refresh());
                    panel.AddTabLine(Color("> Payer en espèces", Colors.Success), "", none, async _ => await PayFromCard(player, panel, req, true));
                    panel.AddTabLine(Color("> Payer par carte bancaire", Colors.Info), "", none, async _ => await PayFromCard(player, panel, req, false));
                    if (req.ClientNetId == player.netId)
                        panel.AddTabLine(Color("Refuser la commande", Colors.Error), "", none, _ =>
                        {
                            CashRegister.Decline(player, req);
                            panel.Refresh();
                        });
                    else if (orders.Count > 1)
                        panel.AddTabLine(Color("Ce n'est pas ma commande", Colors.Grey), "", none, _ =>
                        {
                            player.ClosePanel(panel);
                            Show(player, bizId, false, orders.FirstOrDefault(o => o != req));
                        });
                }
                else if (orders.Count > 1)
                {
                    panel.AddTabLine(Bold(Color("COMMANDES EN ATTENTE - choisissez la vôtre", Colors.Success)), "", none, _ => panel.Refresh());
                    foreach (PaymentRequest order in orders)
                    {
                        PaymentRequest o = order;
                        string content = string.Join(", ", o.Cart.Lines.Select(l => $"{l.Quantity}x {l.Name}"));
                        panel.AddTabLine($"Commande n°{o.OrderNumber} - servie par {o.CashierName}<br>" + Size(Color(content, Colors.Grey), 14),
                            Money(o.Cart.Total), none, _ =>
                            {
                                player.ClosePanel(panel);
                                Show(player, bizId, false, o);
                            });
                    }
                }
                else
                {
                    panel.AddTabLine(Color("Aucune commande à payer : commandez au comptoir !", Colors.Grey), "", none, _ => panel.Refresh());
                }
            }

            // ---- La carte ----
            List<BakeryProduct> shown = products.Values.Where(p => p.Available).OrderBy(p => p.Name).ToList();
            panel.AddTabLine(Bold(Color("PRODUITS", Colors.Orange)), "", none, _ => panel.Refresh());
            if (shown.Count == 0)
                panel.AddTabLine(Color("Aucun produit pour le moment", Colors.Grey), "", none, _ => panel.Refresh());
            foreach (BakeryProduct p in shown)
                panel.AddTabLine(p.Name + "<br>" + Size(StockHelper.Label(p), 14), Money(p.Price), BoulangeriePlugin.ProductIcon(p), _ => panel.Refresh());

            List<BakeryFormula> shownFormulas = formulas.Values.Where(f => f.Available && f.Items.Count > 0).OrderBy(f => f.Name).ToList();
            if (shownFormulas.Count > 0)
            {
                panel.AddTabLine(Bold(Color("FORMULES MAISON", Colors.Orange)), "", none, _ => panel.Refresh());
                foreach (BakeryFormula f in shownFormulas)
                {
                    Dictionary<int, int> items = f.Items;
                    double separate = items.Sum(i => products.TryGetValue(i.Key, out BakeryProduct p) ? p.Price * i.Value : 0);
                    string state = StockHelper.FormulaAvailable(f, products) ? "" : " " + Color("Épuisée", Colors.Error);
                    string saving = separate > f.Price ? Color($" - au lieu de {Money(separate)}", Colors.Success) : "";
                    panel.AddTabLine(f.Name + state + "<br>" + Size(Color(CashRegister.DescribeFormula(f, names), Colors.Grey) + saving, 14),
                        Money(f.Price), BoulangeriePlugin.FormulaIcon(f), _ => panel.Refresh());
                }
            }

            panel.AddTabLine(Size(Color(BoulangeriePlugin.Signature, Colors.Purple), 14), "", none, _ => panel.Refresh());

            panel.AddButton("Sélectionner", _ => panel.SelectTab());
            panel.AddButton("Actualiser", _ => panel.Refresh());
            if (withBack)
                panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async Task PayFromCard(Player player, Panel panel, PaymentRequest req, bool cash)
        {
            if (await CashRegister.Pay(player, req, cash))
                panel.Close();
            else
                panel.Refresh();
        }
    }
}
