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
    public class PaymentPoint : ModEntity<PaymentPoint>, IBizPoint
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
                // Pas de commande : on montre la carte pour que le client sache quoi commander
                MenuCard.Show(player, bizId, false, "Aucune commande à payer. Voici la carte, commandez au comptoir !");
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

        public const string Label = "Paiement";

        public async Task SetProperties(int id)
        {
            PaymentPoint result = await Query(id);
            Id = id;
            TypeName = nameof(PaymentPoint);
            PatternName = result?.PatternName;
            BizId = result?.BizId ?? 0;
        }

        public void CreateOrGenerate(Player player) => BizPointAdmin<PaymentPoint>.CreateOrGenerate(this, player, Label, "Paiement client");

        public void SetPatternData(Player player) => BizPointAdmin<PaymentPoint>.NewPattern(Context, player, Label, "Paiement client");

        public Task GetPatternData(Player player, bool forEdit)
        {
            BizPointAdmin<PaymentPoint>.Patterns(Context, player, Label);
            return Task.CompletedTask;
        }

        public Task GetNPoints(Player player)
        {
            BizPointAdmin<PaymentPoint>.Points(Context, player, Label);
            return Task.CompletedTask;
        }
    }
}
