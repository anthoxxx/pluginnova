using System;
using System.Collections.Generic;
using System.Linq;

namespace Boulangerie
{
    /// <summary>Ligne du panier : un produit ou une formule, avec sa quantité.</summary>
    public class CartLine
    {
        public bool IsFormula;
        public int RefId;
        public string Name;
        public double UnitPrice;
        public int Quantity;

        public double Total => UnitPrice * Quantity;
    }

    /// <summary>Panier en cours d'un boulanger (en mémoire, un par personnage).</summary>
    public class Cart
    {
        public int BizId;
        public string RegisterName;
        public string WebhookUrl;
        public List<CartLine> Lines = new List<CartLine>();
        public int DiscountPercent;

        /// <summary>Demande de paiement en attente pour ce panier (null si aucune).</summary>
        public PaymentRequest Pending;

        public bool IsEmpty => Lines.Count == 0;

        public double SubTotal => Lines.Sum(l => l.Total);

        public double Discount => Math.Round(SubTotal * DiscountPercent / 100.0, 2);

        public double Total => Math.Round(SubTotal - Discount, 2);

        public int ItemCount => Lines.Sum(l => l.Quantity);

        public void Add(bool isFormula, int refId, string name, double unitPrice, int quantity = 1)
        {
            CartLine line = Lines.FirstOrDefault(l => l.IsFormula == isFormula && l.RefId == refId);
            if (line != null)
                line.Quantity += quantity;
            else
                Lines.Add(new CartLine { IsFormula = isFormula, RefId = refId, Name = name, UnitPrice = unitPrice, Quantity = quantity });
        }

        public void RemoveOne(CartLine line)
        {
            line.Quantity--;
            if (line.Quantity <= 0) Lines.Remove(line);
        }

        public void Clear()
        {
            Lines.Clear();
            DiscountPercent = 0;
            Pending = null;
        }

        /// <summary>Copie figée du panier, envoyée au client (le boulanger peut continuer à modifier le sien).</summary>
        public Cart Snapshot()
        {
            return new Cart
            {
                BizId = BizId,
                RegisterName = RegisterName,
                WebhookUrl = WebhookUrl,
                DiscountPercent = DiscountPercent,
                Lines = Lines.Select(l => new CartLine
                {
                    IsFormula = l.IsFormula,
                    RefId = l.RefId,
                    Name = l.Name,
                    UnitPrice = l.UnitPrice,
                    Quantity = l.Quantity
                }).ToList()
            };
        }
    }

    /// <summary>Demande de paiement envoyée par un boulanger à un client.</summary>
    public class PaymentRequest
    {
        public Guid Id = Guid.NewGuid();
        public Cart Cart;
        public int CashierCharacterId;
        public uint CashierNetId;
        public string CashierName;
        /// <summary>0 = commande au comptoir, payable par le premier client au point de paiement.</summary>
        public uint ClientNetId;
        public string ClientName;
        public int OrderNumber;
        /// <summary>Vrai : le client paie au point de paiement ; faux : panel affiché directement.</summary>
        public bool AtPoint;
        public DateTime ExpiresAt;
        public bool Done;
        public ModKit.Helper.Panel ClientPanel;

        public bool Expired => DateTime.UtcNow > ExpiresAt;
    }
}
