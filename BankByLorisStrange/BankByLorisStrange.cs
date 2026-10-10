using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Life;
using Life.DB;
using Life.InventorySystem;
using Life.Network;
using Life.UI;
using Mirror;
using ModKit.Helper;
using ModKit.Helper.PointHelper;
using ModKit.Interfaces;
using ModKit.Internal;
using ModKit.ORM;
using ModKit.Utils;
using Newtonsoft.Json;
using SQLite;
using static ModKit.Helper.TextFormattingHelper;
using Logger = ModKit.Internal.Logger;

namespace BankByLorisStrange
{
    /// <summary>
    /// Bank By Loris Strange : DAB avec frais bancaires configurables, carte bleue (item configurable)
    /// et code de carte (PIN) généré pour chaque joueur.
    /// </summary>
    public class BankPlugin : ModKit.ModKit
    {
        public const string Title = "Bank By Loris Strange";

        public static BankPlugin Instance { get; private set; }

        public BankConfig Config { get; private set; } = new BankConfig();

        private string _configPath;

        public BankPlugin(IGameAPI api) : base(api)
        {
            PluginInformations = new PluginInformations(AssemblyHelper.GetName(), "1.0.0", "Loris Strange");
            Instance = this;
        }

        public override void OnPluginInit()
        {
            base.OnPluginInit();

            _configPath = Path.Combine(DirectoryPath, "BankByLorisStrange", "config.json");
            LoadConfig();

            Orm.RegisterTable<BankAccount>();
            Orm.RegisterTable<BankTransaction>();
            Orm.RegisterTable<AtmPattern>();

            // DAB placés par le staff (AAMenu > Administration > Points bleus)
            AtmPattern pattern = new AtmPattern(false);
            PointHelper.AddPattern(nameof(AtmPattern), pattern);

            if (AAMenu.AAMenu.menu != null)
            {
                AAMenu.AAMenu.menu.AddBuilder(PluginInformations, "DAB (Bank)", pattern, this);
                AAMenu.Menu.AddAdminPluginTabLine(PluginInformations, Config.AdminLevel, Title,
                    ui => BankAdmin.MainMenu(this, PanelHelper.ReturnPlayerFromPanel(ui)));
                AAMenu.Menu.AddInteractionTabLine(PluginInformations, "Ma carte bancaire",
                    ui => BankMenus.CardMenu(this, PanelHelper.ReturnPlayerFromPanel(ui)));
            }
            else
            {
                Logger.LogWarning(PluginInformations.SourceName, "AAMenu introuvable : utilisez /bank (staff) et /macarte (joueurs).");
            }

            // Frais sur les DAB d'origine du jeu
            Nova.server.OnPlayerBankEvent += GameAtmHook.OnPlayerBank;
            Nova.server.OnPlayerMoneyEvent += GameAtmHook.OnPlayerMoney;

            RegisterCommands();

            Logger.LogSuccess($"{PluginInformations.SourceName} v{PluginInformations.Version}", "initialisé");
        }

        public override void OnPlayerSpawnCharacter(Player player, NetworkConnection conn, Characters character)
        {
            base.OnPlayerSpawnCharacter(player, conn, character);
            PointHelper.InitAllNPoint(player);
            // Chaque joueur reçoit automatiquement un compte avec un code de carte bleue
            _ = BankService.GetOrCreateAccount(player, true);
        }

        private void RegisterCommands()
        {
            new SChatCommand("/macarte", new[] { "/carte" }, "Voir ma carte bancaire et mon code", "/macarte",
                (Action<Player, string[]>)((player, args) => BankMenus.CardMenu(this, player))).Register();

            new SChatCommand("/bank", "Administration de la banque (staff)", "/bank",
                (Action<Player, string[]>)((player, args) =>
                {
                    if (!BankService.IsStaff(player))
                    {
                        player.Notify(Title, "Vous devez être staff et en service admin.", NotificationManager.Type.Error, 5f);
                        return;
                    }
                    BankAdmin.MainMenu(this, player);
                })).Register();
        }

        // ------------------------------------------------------------------
        //  Configuration (JSON)
        // ------------------------------------------------------------------

        public void LoadConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath));
                if (File.Exists(_configPath))
                    Config = JsonConvert.DeserializeObject<BankConfig>(File.ReadAllText(_configPath)) ?? new BankConfig();
                SaveConfig(); // ajoute les nouvelles clés éventuelles
            }
            catch (Exception ex)
            {
                Logger.LogError(PluginInformations.SourceName, $"config.json invalide, valeurs par défaut utilisées : {ex.Message}");
                Config = new BankConfig();
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
                Logger.LogError(PluginInformations.SourceName, $"Impossible d'écrire config.json : {ex.Message}");
            }
        }
    }

    /// <summary>Contenu de Plugins/ModKit/BankByLorisStrange/config.json (modifiable aussi dans AAMenu).</summary>
    public class BankConfig
    {
        // Frais de retrait
        public double WithdrawFeePercent = 2.0;
        public double WithdrawFeeFixed = 1.0;
        public double WithdrawFeeMin = 1.0;
        /// <summary>0 = pas de plafond.</summary>
        public double WithdrawFeeMax = 250.0;

        // Frais de dépôt (DAB du plugin)
        public double DepositFeePercent = 0.0;
        public double DepositFeeFixed = 0.0;

        /// <summary>Montant maximum retiré par jour et par joueur sur les DAB du plugin (0 = illimité).</summary>
        public double DailyWithdrawLimit = 5000.0;

        /// <summary>Appliquer les frais de retrait sur les DAB d'origine du jeu.</summary>
        public bool ApplyFeesOnGameAtm = true;

        /// <summary>Item « carte bancaire » exigé aux DAB du plugin (0 = aucune carte exigée).</summary>
        public int CardItemId = 0;
        /// <summary>Donner une carte à la création du compte.</summary>
        public bool GiveCardOnAccountCreation = true;
        /// <summary>Prix d'une nouvelle carte commandée au DAB.</summary>
        public double NewCardPrice = 50.0;

        // Sécurité du code
        public int MaxPinAttempts = 3;
        public int BlockMinutes = 10;
        /// <summary>Le DAB avale la carte après trop d'erreurs de code.</summary>
        public bool SwallowCardOnBlock = true;

        /// <summary>Id de l'entreprise qui touche les frais (0 = les frais disparaissent).</summary>
        public int FeesBizId = 0;

        /// <summary>Niveau admin minimum pour le menu staff dans AAMenu.</summary>
        public int AdminLevel = 1;
    }

    // ======================================================================
    //  Base de données
    // ======================================================================

    /// <summary>Compte bancaire du plugin : un par personnage, avec numéro de carte et code.</summary>
    public class BankAccount : ModEntity<BankAccount>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int CharacterId { get; set; }
        public string OwnerName { get; set; }
        public string CardNumber { get; set; }
        public string Pin { get; set; }
        public int FailedAttempts { get; set; }
        /// <summary>Timestamp unix jusqu'auquel la carte est bloquée (trop d'erreurs de code).</summary>
        public long BlockedUntil { get; set; }
        /// <summary>Opposition (par le joueur ou le staff) : la carte est refusée jusqu'à déblocage.</summary>
        public bool Opposed { get; set; }
        public string WithdrawDay { get; set; }
        public double WithdrawnToday { get; set; }
        public double TotalFeesPaid { get; set; }
        public long CreatedAt { get; set; }

        [Ignore]
        public string MaskedCardNumber => string.IsNullOrEmpty(CardNumber) || CardNumber.Length < 4
            ? "****"
            : "**** **** **** " + CardNumber.Substring(CardNumber.Length - 4);

        [Ignore]
        public string FormattedCardNumber => string.IsNullOrEmpty(CardNumber)
            ? "?"
            : string.Join(" ", Enumerable.Range(0, CardNumber.Length / 4).Select(i => CardNumber.Substring(i * 4, 4)));
    }

    /// <summary>Historique des opérations bancaires effectuées au DAB.</summary>
    public class BankTransaction : ModEntity<BankTransaction>
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public int CharacterId { get; set; }
        public string Type { get; set; }
        public double Amount { get; set; }
        public double Fee { get; set; }
        public long Date { get; set; }
    }

    // ======================================================================
    //  Logique bancaire
    // ======================================================================

    public static class BankService
    {
        private static BankConfig Config => BankPlugin.Instance.Config;

        /// <summary>Vrai pendant une opération du plugin, pour que le hook des DAB du jeu l'ignore.</summary>
        public static bool InternalOperation;

        public static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public static bool IsStaff(Player player) => player.IsAdmin && player.serviceAdmin;

        public static Player OnlinePlayer(int characterId) => Nova.server.Players.FirstOrDefault(p => p?.character != null && p.character.Id == characterId);

        public static string Money(double amount) => $"{amount:0.##}€";

        // --- Comptes ---------------------------------------------------------

        public static async Task<BankAccount> GetAccount(int characterId)
        {
            List<BankAccount> accounts = await BankAccount.Query(a => a.CharacterId == characterId);
            return accounts.FirstOrDefault();
        }

        public static async Task<BankAccount> GetOrCreateAccount(Player player, bool notify)
        {
            int characterId = player.character.Id;
            BankAccount account = await GetAccount(characterId);
            if (account != null)
            {
                if (account.OwnerName != player.FullName)
                {
                    account.OwnerName = player.FullName;
                    await account.Save();
                }
                return account;
            }

            account = new BankAccount
            {
                CharacterId = characterId,
                OwnerName = player.FullName,
                CardNumber = GenerateCardNumber(),
                Pin = GeneratePin(),
                CreatedAt = Now
            };
            if (!await account.Save())
            {
                Logger.LogError(BankPlugin.Title, $"Impossible de créer le compte de {player.FullName}");
                return null;
            }

            if (Config.GiveCardOnAccountCreation && Config.CardItemId > 0)
                InventoryUtils.AddItem(player, Config.CardItemId, 1);

            if (notify)
                SendPin(player, account, "Bienvenue à la banque ! Votre carte bleue a été créée.");
            return account;
        }

        public static void SendPin(Player player, BankAccount account, string header)
        {
            player.Notify(BankPlugin.Title, $"{header}\nCode : {account.Pin}", NotificationManager.Type.Success, 15f);
            player.SendText($"<color=#42d4f4>[{BankPlugin.Title}]</color> {header} Carte {account.FormattedCardNumber} - code : <b>{account.Pin}</b> (/macarte pour le revoir)");
        }

        public static async Task<bool> RegeneratePin(BankAccount account, Player owner)
        {
            account.Pin = GeneratePin();
            account.FailedAttempts = 0;
            account.BlockedUntil = 0;
            bool saved = await account.Save();
            if (saved && owner != null)
                SendPin(owner, account, "Votre code de carte bleue a été renouvelé.");
            return saved;
        }

        public static string GeneratePin() => RandomDigits(4);

        /// <summary>Numéro à 16 chiffres valide selon l'algorithme de Luhn, préfixe 4970 (CB).</summary>
        public static string GenerateCardNumber()
        {
            string body = "4970" + RandomDigits(11);
            int sum = 0;
            for (int i = 0; i < body.Length; i++)
            {
                int digit = body[body.Length - 1 - i] - '0';
                if (i % 2 == 0)
                {
                    digit *= 2;
                    if (digit > 9) digit -= 9;
                }
                sum += digit;
            }
            return body + ((10 - sum % 10) % 10);
        }

        private static string RandomDigits(int count)
        {
            byte[] bytes = new byte[count];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return new string(bytes.Select(b => (char)('0' + b % 10)).ToArray());
        }

        // --- Frais -------------------------------------------------------------

        public static double WithdrawFee(double amount)
        {
            if (amount <= 0 || (Config.WithdrawFeePercent <= 0 && Config.WithdrawFeeFixed <= 0)) return 0;
            double fee = Config.WithdrawFeeFixed + amount * Config.WithdrawFeePercent / 100.0;
            fee = Math.Max(fee, Config.WithdrawFeeMin);
            if (Config.WithdrawFeeMax > 0) fee = Math.Min(fee, Config.WithdrawFeeMax);
            return Math.Round(fee, 2);
        }

        public static double DepositFee(double amount)
        {
            if (amount <= 0) return 0;
            double fee = Config.DepositFeeFixed + amount * Config.DepositFeePercent / 100.0;
            return Math.Round(Math.Min(Math.Max(fee, 0), amount), 2);
        }

        public static string FeesDescription()
        {
            if (Config.WithdrawFeePercent <= 0 && Config.WithdrawFeeFixed <= 0) return "Retraits sans frais";
            string text = $"Frais de retrait : {Config.WithdrawFeePercent:0.##}% + {Money(Config.WithdrawFeeFixed)}";
            text += $" (min {Money(Config.WithdrawFeeMin)}";
            if (Config.WithdrawFeeMax > 0) text += $", max {Money(Config.WithdrawFeeMax)}";
            return text + ")";
        }

        private static void PayFeesToBiz(double fee)
        {
            if (fee <= 0 || Config.FeesBizId <= 0) return;
            Bizs biz = Nova.biz.FetchBiz(Config.FeesBizId);
            biz?.AddBankMoney(fee, "Frais bancaires");
        }

        // --- Opérations ----------------------------------------------------------

        public static double RemainingDailyLimit(BankAccount account)
        {
            if (Config.DailyWithdrawLimit <= 0) return double.MaxValue;
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            double withdrawn = account.WithdrawDay == today ? account.WithdrawnToday : 0;
            return Math.Max(0, Config.DailyWithdrawLimit - withdrawn);
        }

        public static async Task<bool> Withdraw(Player player, BankAccount account, double amount)
        {
            amount = Math.Round(amount, 2);
            if (amount <= 0)
            {
                player.Notify(BankPlugin.Title, "Montant invalide.", NotificationManager.Type.Error, 5f);
                return false;
            }

            if (amount > RemainingDailyLimit(account))
            {
                player.Notify(BankPlugin.Title, $"Plafond journalier atteint. Reste aujourd'hui : {Money(RemainingDailyLimit(account))}.", NotificationManager.Type.Warning, 6f);
                return false;
            }

            double fee = WithdrawFee(amount);
            double total = amount + fee;
            if (player.Bank < total)
            {
                player.Notify(BankPlugin.Title, $"Solde insuffisant : il faut {Money(total)} ({Money(amount)} + {Money(fee)} de frais).", NotificationManager.Type.Error, 6f);
                return false;
            }
            if (!player.CanAddMoney(amount))
            {
                player.Notify(BankPlugin.Title, "Vous n'avez pas assez de place pour cet argent liquide.", NotificationManager.Type.Error, 5f);
                return false;
            }

            InternalOperation = true;
            try
            {
                player.AddBankMoney(-total, "Retrait DAB");
                player.AddMoney(amount, "Retrait DAB");
            }
            finally
            {
                InternalOperation = false;
            }
            PayFeesToBiz(fee);

            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (account.WithdrawDay != today)
            {
                account.WithdrawDay = today;
                account.WithdrawnToday = 0;
            }
            account.WithdrawnToday += amount;
            account.TotalFeesPaid += fee;
            await account.Save();
            await Log(account.CharacterId, "Retrait", amount, fee);

            player.Notify(BankPlugin.Title, $"Retrait de {Money(amount)} effectué. Frais : {Money(fee)}.", NotificationManager.Type.Success, 6f);
            return true;
        }

        public static async Task<bool> Deposit(Player player, BankAccount account, double amount)
        {
            amount = Math.Round(amount, 2);
            if (amount <= 0)
            {
                player.Notify(BankPlugin.Title, "Montant invalide.", NotificationManager.Type.Error, 5f);
                return false;
            }
            if (player.Money < amount)
            {
                player.Notify(BankPlugin.Title, "Vous n'avez pas assez d'argent liquide.", NotificationManager.Type.Error, 5f);
                return false;
            }

            double fee = DepositFee(amount);
            InternalOperation = true;
            try
            {
                player.AddMoney(-amount, "Dépôt DAB");
                player.AddBankMoney(amount - fee, "Dépôt DAB");
            }
            finally
            {
                InternalOperation = false;
            }
            PayFeesToBiz(fee);

            account.TotalFeesPaid += fee;
            await account.Save();
            await Log(account.CharacterId, "Dépôt", amount, fee);

            player.Notify(BankPlugin.Title, $"Dépôt de {Money(amount)} effectué." + (fee > 0 ? $" Frais : {Money(fee)}." : ""), NotificationManager.Type.Success, 6f);
            return true;
        }

        /// <summary>Prélève des frais après un retrait sur un DAB d'origine du jeu.</summary>
        public static async void ChargeGameAtmFee(Player bankOwner, Player atmUser, double amount)
        {
            double fee = WithdrawFee(amount);
            if (fee <= 0) return;

            InternalOperation = true;
            try
            {
                if (bankOwner.Bank >= fee)
                    bankOwner.AddBankMoney(-fee, "Frais bancaires");
                else if (atmUser.Money >= fee)
                    atmUser.AddMoney(-fee, "Frais bancaires");
                else
                    bankOwner.AddBankMoney(-fee, "Frais bancaires"); // découvert
            }
            finally
            {
                InternalOperation = false;
            }
            PayFeesToBiz(fee);

            atmUser.Notify(BankPlugin.Title, $"Frais bancaires sur ce retrait : {Money(fee)}.", NotificationManager.Type.Info, 6f);

            BankAccount account = await GetAccount(bankOwner.character.Id);
            if (account != null)
            {
                account.TotalFeesPaid += fee;
                await account.Save();
            }
            await Log(bankOwner.character.Id, "Retrait (DAB du jeu)", amount, fee);
        }

        public static Task<bool> Log(int characterId, string type, double amount, double fee)
        {
            return new BankTransaction { CharacterId = characterId, Type = type, Amount = amount, Fee = fee, Date = Now }.Save();
        }

        public static async Task<List<BankTransaction>> History(int characterId, int count)
        {
            List<BankTransaction> list = await BankTransaction.Query(t => t.CharacterId == characterId);
            return list.OrderByDescending(t => t.Date).Take(count).ToList();
        }

        // --- Carte (item) ------------------------------------------------------

        public static bool HasCard(Player player)
        {
            return Config.CardItemId <= 0 || InventoryUtils.CheckInventoryContainsItem(player, Config.CardItemId, 1);
        }

        public static string CardItemName(Player player)
        {
            if (Config.CardItemId <= 0) return "Aucune (pas de carte exigée)";
            Item item = ItemUtils.GetItemById(Config.CardItemId);
            string name = item != null ? (player != null ? player.NewTranslate("Items", item.itemName) : item.itemName) : "Item introuvable";
            return $"{name} (#{Config.CardItemId})";
        }

        public static string FormatDate(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("dd/MM HH:mm");
    }

    /// <summary>
    /// Détecte les retraits faits sur les DAB d'origine du jeu : le jeu appelle
    /// AddBankMoney(-X, raison) sur le titulaire puis AddMoney(+X, raison) sur l'utilisateur
    /// du DAB, avec la même raison, dans la même frame.
    /// </summary>
    public static class GameAtmHook
    {
        private static Player _lastBankPlayer;
        private static double _lastBankAmount;
        private static string _lastBankReason;
        private static int _lastBankFrame = -1;

        public static void OnPlayerBank(Player player, double amount, string reason)
        {
            if (BankService.InternalOperation) return;
            _lastBankPlayer = player;
            _lastBankAmount = amount;
            _lastBankReason = reason;
            _lastBankFrame = UnityEngine.Time.frameCount;
        }

        public static void OnPlayerMoney(Player player, double amount, string reason)
        {
            if (BankService.InternalOperation || amount <= 0) return;
            if (!BankPlugin.Instance.Config.ApplyFeesOnGameAtm) return;

            bool isGameAtmWithdraw = _lastBankFrame == UnityEngine.Time.frameCount
                && _lastBankPlayer != null
                && _lastBankReason == reason
                && Math.Abs(_lastBankAmount + amount) < 0.001;
            if (!isGameAtmWithdraw) return;

            Player bankOwner = _lastBankPlayer;
            _lastBankPlayer = null;
            _lastBankFrame = -1;
            try
            {
                BankService.ChargeGameAtmFee(bankOwner, player, amount);
            }
            catch (Exception ex)
            {
                Logger.LogError(BankPlugin.Title, $"Frais DAB du jeu : {ex}");
            }
        }
    }

    // ======================================================================
    //  Menus joueurs (DAB et carte)
    // ======================================================================

    public static class BankMenus
    {
        private static BankConfig Config => BankPlugin.Instance.Config;

        /// <summary>Insertion de la carte dans un DAB.</summary>
        public static async void OpenAtm(ModKit.ModKit context, Player player, string atmName)
        {
            BankAccount account = await BankService.GetOrCreateAccount(player, true);
            if (account == null) return;

            if (!BankService.HasCard(player))
            {
                player.Notify(atmName, $"Insérez votre carte bancaire : il vous faut l'item {BankService.CardItemName(player)}.", NotificationManager.Type.Error, 6f);
                return;
            }
            if (account.Opposed)
            {
                player.Notify(atmName, "Carte refusée : votre carte est en opposition. Commandez-en une nouvelle via /macarte ou contactez le staff.", NotificationManager.Type.Error, 7f);
                return;
            }
            if (account.BlockedUntil > BankService.Now)
            {
                long minutes = Math.Max(1, (account.BlockedUntil - BankService.Now + 59) / 60);
                player.Notify(atmName, $"Carte bloquée suite à trop d'erreurs de code. Réessayez dans {minutes} min.", NotificationManager.Type.Error, 7f);
                return;
            }

            AskPin(context, player, account, atmName);
        }

        private static void AskPin(ModKit.ModKit context, Player player, BankAccount account, string atmName)
        {
            Panel panel = context.PanelHelper.Create($"{atmName} - Code", UIPanel.PanelType.Input, player, () => AskPin(context, player, account, atmName));
            panel.TextLines.Add($"Carte {account.MaskedCardNumber}");
            panel.TextLines.Add("Composez votre code à 4 chiffres");
            if (account.FailedAttempts > 0)
                panel.TextLines.Add(Color($"Code erroné : {Config.MaxPinAttempts - account.FailedAttempts} essai(s) restant(s)", Colors.Warning));
            panel.SetInputPlaceholder("••••");
            panel.isPassword = true;

            panel.NextButton("Valider", async () => await CheckPin(context, player, account, atmName, panel.inputText?.Trim()));
            panel.CloseButton("Reprendre la carte");
            panel.Display();
        }

        private static async Task CheckPin(ModKit.ModKit context, Player player, BankAccount account, string atmName, string pin)
        {
            if (pin == account.Pin)
            {
                account.FailedAttempts = 0;
                await account.Save();
                AtmMenu(context, player, account, atmName);
                return;
            }

            account.FailedAttempts++;
            if (account.FailedAttempts >= Math.Max(1, Config.MaxPinAttempts))
            {
                account.FailedAttempts = 0;
                account.BlockedUntil = BankService.Now + Math.Max(1, Config.BlockMinutes) * 60L;
                await account.Save();

                string message = $"Code erroné {Config.MaxPinAttempts} fois : carte bloquée {Config.BlockMinutes} min.";
                if (Config.SwallowCardOnBlock && Config.CardItemId > 0 && InventoryUtils.RemoveFromInventory(player, Config.CardItemId, 1) > 0)
                    message += " Le DAB a avalé votre carte !";
                player.Notify(atmName, message, NotificationManager.Type.Error, 8f);
                return;
            }

            await account.Save();
            player.Notify(atmName, "Code erroné.", NotificationManager.Type.Warning, 4f);
            AskPin(context, player, account, atmName);
        }

        public static void AtmMenu(ModKit.ModKit context, Player player, BankAccount account, string atmName)
        {
            Panel panel = context.PanelHelper.Create(atmName, UIPanel.PanelType.TabPrice, player, () => AtmMenu(context, player, account, atmName));

            panel.AddTabLine("Consulter mon solde", BankService.Money(player.Bank), IconUtils.Others.None.Id, _ => ShowBalance(context, player, account, atmName));
            panel.AddTabLine("Retirer de l'argent", Color(BankService.FeesDescription(), Colors.Grey), IconUtils.Others.None.Id, _ => QuickWithdraw(context, player, account, atmName));
            panel.AddTabLine("Déposer de l'argent", BankService.Money(player.Money) + " en poche", IconUtils.Others.None.Id, _ => AskDeposit(context, player, account, atmName));
            panel.AddTabLine("Dernières opérations", "", IconUtils.Others.None.Id, async _ => await ShowHistory(context, player, account, atmName));
            panel.AddTabLine("Changer mon code", "", IconUtils.Others.None.Id, _ => ChangePin(context, player, account, atmName));
            if (Config.CardItemId > 0)
                panel.AddTabLine("Commander une nouvelle carte", BankService.Money(Config.NewCardPrice), IconUtils.Others.None.Id, async _ => await OrderCard(player, account, atmName));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.CloseButton("Reprendre la carte");
            panel.Display();
        }

        private static void ShowBalance(ModKit.ModKit context, Player player, BankAccount account, string atmName)
        {
            Panel panel = context.PanelHelper.Create($"{atmName} - Solde", UIPanel.PanelType.Text, player, () => ShowBalance(context, player, account, atmName));
            panel.TextLines.Add($"Titulaire : {player.FullName}");
            panel.TextLines.Add($"Carte : {account.MaskedCardNumber}");
            panel.TextLines.Add("");
            panel.TextLines.Add(Size(Bold($"Solde : {BankService.Money(player.Bank)}"), 26));
            panel.TextLines.Add($"Argent liquide : {BankService.Money(player.Money)}");
            if (Config.DailyWithdrawLimit > 0)
                panel.TextLines.Add($"Retrait possible aujourd'hui : {BankService.Money(BankService.RemainingDailyLimit(account))}");
            panel.TextLines.Add($"Total des frais payés : {BankService.Money(account.TotalFeesPaid)}");
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static readonly double[] QuickAmounts = { 20, 50, 100, 200, 500, 1000 };

        private static void QuickWithdraw(ModKit.ModKit context, Player player, BankAccount account, string atmName)
        {
            Panel panel = context.PanelHelper.Create($"{atmName} - Retrait", UIPanel.PanelType.TabPrice, player, () => QuickWithdraw(context, player, account, atmName));
            panel.TextLines.Add(BankService.FeesDescription());

            foreach (double amount in QuickAmounts)
            {
                double fee = BankService.WithdrawFee(amount);
                panel.AddTabLine(BankService.Money(amount), Color($"+{BankService.Money(fee)} de frais", Colors.Grey), IconUtils.Others.None.Id, async _ =>
                {
                    if (await BankService.Withdraw(player, account, amount))
                        panel.Refresh();
                });
            }
            panel.AddTabLine("Autre montant", "", IconUtils.Others.None.Id, _ => AskWithdraw(context, player, account, atmName));

            panel.AddButton("Retirer", _ => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void AskWithdraw(ModKit.ModKit context, Player player, BankAccount account, string atmName)
        {
            Panel panel = context.PanelHelper.Create($"{atmName} - Autre montant", UIPanel.PanelType.Input, player, () => AskWithdraw(context, player, account, atmName));
            panel.TextLines.Add($"Solde : {BankService.Money(player.Bank)}");
            panel.TextLines.Add(BankService.FeesDescription());
            panel.SetInputPlaceholder("Montant à retirer");

            panel.PreviousButtonWithAction("Retirer", async () =>
                TryParseAmount(panel.inputText, out double amount) && await BankService.Withdraw(player, account, amount));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void AskDeposit(ModKit.ModKit context, Player player, BankAccount account, string atmName)
        {
            Panel panel = context.PanelHelper.Create($"{atmName} - Dépôt", UIPanel.PanelType.Input, player, () => AskDeposit(context, player, account, atmName));
            panel.TextLines.Add($"Argent liquide : {BankService.Money(player.Money)}");
            if (Config.DepositFeePercent > 0 || Config.DepositFeeFixed > 0)
                panel.TextLines.Add($"Frais de dépôt : {Config.DepositFeePercent:0.##}% + {BankService.Money(Config.DepositFeeFixed)}");
            panel.SetInputPlaceholder("Montant à déposer");

            panel.PreviousButtonWithAction("Déposer", async () =>
                TryParseAmount(panel.inputText, out double amount) && await BankService.Deposit(player, account, amount));
            panel.PreviousButtonWithAction("Tout déposer", async () => await BankService.Deposit(player, account, Math.Floor(player.Money * 100) / 100));
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async Task ShowHistory(ModKit.ModKit context, Player player, BankAccount account, string atmName)
        {
            List<BankTransaction> history = await BankService.History(account.CharacterId, 15);
            Panel panel = context.PanelHelper.Create($"{atmName} - Opérations", UIPanel.PanelType.TabPrice, player, async () => await ShowHistory(context, player, account, atmName));

            if (history.Count == 0)
                panel.AddTabLine("Aucune opération", _ => { });
            foreach (BankTransaction t in history)
            {
                string fee = t.Fee > 0 ? Color($" (frais {BankService.Money(t.Fee)})", Colors.Grey) : "";
                panel.AddTabLine($"{BankService.FormatDate(t.Date)} - {t.Type}", BankService.Money(t.Amount) + fee, IconUtils.Others.None.Id, _ => { });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void ChangePin(ModKit.ModKit context, Player player, BankAccount account, string atmName)
        {
            Panel panel = context.PanelHelper.Create($"{atmName} - Nouveau code", UIPanel.PanelType.Input, player, () => ChangePin(context, player, account, atmName));
            panel.TextLines.Add("Choisissez un nouveau code à 4 chiffres");
            panel.SetInputPlaceholder("••••");
            panel.isPassword = true;

            panel.PreviousButtonWithAction("Valider", async () =>
            {
                string pin = panel.inputText?.Trim() ?? "";
                if (pin.Length != 4 || !pin.All(char.IsDigit))
                {
                    player.Notify(atmName, "Le code doit contenir exactement 4 chiffres.", NotificationManager.Type.Error, 5f);
                    return false;
                }
                account.Pin = pin;
                if (!await account.Save()) return false;
                player.Notify(atmName, "Votre code a été modifié.", NotificationManager.Type.Success, 5f);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async Task OrderCard(Player player, BankAccount account, string title)
        {
            double price = Math.Max(0, Config.NewCardPrice);
            if (player.Bank < price)
            {
                player.Notify(title, $"Solde insuffisant ({BankService.Money(price)}).", NotificationManager.Type.Error, 5f);
                return;
            }
            if (!InventoryUtils.AddItem(player, Config.CardItemId, 1))
            {
                player.Notify(title, "Inventaire plein.", NotificationManager.Type.Error, 5f);
                return;
            }
            if (price > 0)
            {
                BankService.InternalOperation = true;
                try { player.AddBankMoney(-price, "Nouvelle carte bancaire"); }
                finally { BankService.InternalOperation = false; }
            }

            // Nouvelle carte = nouveau numéro, l'opposition est levée
            account.CardNumber = BankService.GenerateCardNumber();
            account.Opposed = false;
            account.FailedAttempts = 0;
            account.BlockedUntil = 0;
            await account.Save();
            await BankService.Log(account.CharacterId, "Nouvelle carte", price, 0);
            player.Notify(title, $"Nouvelle carte {account.MaskedCardNumber} reçue. Votre code reste le même.", NotificationManager.Type.Success, 6f);
        }

        /// <summary>/macarte ou AAMenu > Interaction : voir sa carte, son code, faire opposition.</summary>
        public static async void CardMenu(ModKit.ModKit context, Player player)
        {
            BankAccount account = await BankService.GetOrCreateAccount(player, true);
            if (account == null) return;

            Panel panel = context.PanelHelper.Create("Ma carte bancaire", UIPanel.PanelType.Tab, player, () => CardMenu(context, player));
            panel.TextLines.Add(Bold(BankPlugin.Title));
            panel.TextLines.Add($"Titulaire : {player.FullName}");
            panel.TextLines.Add($"N° : {account.FormattedCardNumber}");
            panel.TextLines.Add($"Code : {Bold(account.Pin)}");
            string state = account.Opposed ? Color("En opposition", Colors.Error)
                : account.BlockedUntil > BankService.Now ? Color("Bloquée temporairement", Colors.Warning)
                : Color("Active", Colors.Success);
            panel.TextLines.Add($"État : {state}");
            panel.TextLines.Add(BankService.FeesDescription());

            panel.AddTabLine($"Voir mon code : {account.Pin}", _ => BankService.SendPin(player, account, "Rappel de votre carte."));
            if (!account.Opposed)
            {
                panel.AddTabLine(Color("Faire opposition (carte perdue / volée)", Colors.Error), async _ =>
                {
                    account.Opposed = true;
                    await account.Save();
                    player.Notify(BankPlugin.Title, "Opposition enregistrée : votre carte ne fonctionne plus. Commandez-en une nouvelle à un DAB ou ici.", NotificationManager.Type.Warning, 7f);
                    panel.Refresh();
                });
            }
            if (Config.CardItemId > 0)
            {
                panel.AddTabLine($"Commander une nouvelle carte ({BankService.Money(Config.NewCardPrice)})", async _ =>
                {
                    await OrderCard(player, account, BankPlugin.Title);
                    panel.Refresh();
                });
            }

            panel.AddButton("Sélectionner", _ => panel.SelectTab());
            panel.CloseButton();
            panel.Display();
        }

        public static bool TryParseAmount(string input, out double amount)
        {
            amount = 0;
            if (string.IsNullOrWhiteSpace(input)) return false;
            return double.TryParse(input.Replace(',', '.').Replace("€", "").Trim(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out amount) && amount > 0;
        }
    }

    // ======================================================================
    //  Menu staff (AAMenu > Administration > Plugins > Bank By Loris Strange)
    // ======================================================================

    public static class BankAdmin
    {
        private static BankConfig Config => BankPlugin.Instance.Config;

        public static async void MainMenu(BankPlugin context, Player player)
        {
            if (player == null || !player.IsAdmin) return;

            List<BankAccount> accounts = await BankAccount.QueryAll();
            Panel panel = context.PanelHelper.Create(BankPlugin.Title, UIPanel.PanelType.Tab, player, () => MainMenu(context, player));
            panel.TextLines.Add($"{accounts.Count} compte(s) - frais perçus : {BankService.Money(accounts.Sum(a => a.TotalFeesPaid))}");

            panel.AddTabLine(Color("Frais et réglages", Colors.Info), _ => ConfigMenu(context, player));
            panel.AddTabLine($"Item carte bancaire : {BankService.CardItemName(player)}", _ => SetCardItem(context, player));
            panel.AddTabLine("Joueurs connectés (codes, cartes)", _ => PlayersMenu(context, player));
            panel.AddTabLine("Générer les codes des joueurs connectés", async _ =>
            {
                int created = 0;
                foreach (Player target in Nova.server.Players.ToList())
                {
                    if (target?.character == null) continue;
                    if (await BankService.GetAccount(target.character.Id) == null && await BankService.GetOrCreateAccount(target, true) != null)
                        created++;
                }
                player.Notify(BankPlugin.Title, $"{created} code(s) de carte généré(s). Les autres joueurs en avaient déjà un.", NotificationManager.Type.Success, 6f);
            });
            panel.AddTabLine(Color("Régénérer TOUS les codes", Colors.Warning), _ => ConfirmRegenerateAll(context, player));
            panel.AddTabLine("Placer un DAB à ma position", _ => new AtmPattern(false) { Context = context }.CreateOrGenerate(player));
            panel.AddTabLine("Recharger config.json", _ =>
            {
                context.LoadConfig();
                player.Notify(BankPlugin.Title, "Configuration rechargée.", NotificationManager.Type.Success, 4f);
            });

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private class Setting
        {
            public string Label;
            public Func<string> Get;
            public Func<string, bool> Set;
        }

        private static Setting Number(string label, Func<double> get, Action<double> set) => new Setting
        {
            Label = label,
            Get = () => $"{get():0.##}",
            Set = s => { if (!BankMenus.TryParseAmount(s, out double v) && s?.Trim() != "0") return false; set(Math.Max(0, v)); return true; }
        };

        private static Setting Integer(string label, Func<int> get, Action<int> set) => new Setting
        {
            Label = label,
            Get = () => get().ToString(),
            Set = s => { if (!int.TryParse(s?.Trim(), out int v) || v < 0) return false; set(v); return true; }
        };

        private static Setting Toggle(string label, Func<bool> get, Action<bool> set) => new Setting
        {
            Label = label,
            Get = () => get() ? "Oui" : "Non",
            Set = null
        };

        private static List<(Setting setting, Action toggle)> Settings()
        {
            return new List<(Setting, Action)>
            {
                (Number("Frais de retrait (%)", () => Config.WithdrawFeePercent, v => Config.WithdrawFeePercent = v), null),
                (Number("Frais de retrait fixes (€)", () => Config.WithdrawFeeFixed, v => Config.WithdrawFeeFixed = v), null),
                (Number("Frais de retrait minimum (€)", () => Config.WithdrawFeeMin, v => Config.WithdrawFeeMin = v), null),
                (Number("Frais de retrait maximum (€, 0 = aucun)", () => Config.WithdrawFeeMax, v => Config.WithdrawFeeMax = v), null),
                (Number("Frais de dépôt (%)", () => Config.DepositFeePercent, v => Config.DepositFeePercent = v), null),
                (Number("Frais de dépôt fixes (€)", () => Config.DepositFeeFixed, v => Config.DepositFeeFixed = v), null),
                (Number("Plafond de retrait / jour (€, 0 = aucun)", () => Config.DailyWithdrawLimit, v => Config.DailyWithdrawLimit = v), null),
                (Toggle("Frais sur les DAB du jeu", () => Config.ApplyFeesOnGameAtm, null), () => Config.ApplyFeesOnGameAtm = !Config.ApplyFeesOnGameAtm),
                (Number("Prix d'une nouvelle carte (€)", () => Config.NewCardPrice, v => Config.NewCardPrice = v), null),
                (Toggle("Carte offerte à la création du compte", () => Config.GiveCardOnAccountCreation, null), () => Config.GiveCardOnAccountCreation = !Config.GiveCardOnAccountCreation),
                (Integer("Essais de code avant blocage", () => Config.MaxPinAttempts, v => Config.MaxPinAttempts = Math.Max(1, v)), null),
                (Integer("Durée du blocage (minutes)", () => Config.BlockMinutes, v => Config.BlockMinutes = Math.Max(1, v)), null),
                (Toggle("Le DAB avale la carte au blocage", () => Config.SwallowCardOnBlock, null), () => Config.SwallowCardOnBlock = !Config.SwallowCardOnBlock),
                (Integer("Entreprise qui touche les frais (id, 0 = aucune)", () => Config.FeesBizId, v => Config.FeesBizId = v), null),
            };
        }

        private static void ConfigMenu(BankPlugin context, Player player)
        {
            Panel panel = context.PanelHelper.Create($"{BankPlugin.Title} - Réglages", UIPanel.PanelType.TabPrice, player, () => ConfigMenu(context, player));

            foreach ((Setting setting, Action toggle) in Settings())
            {
                panel.AddTabLine(setting.Label, Color(setting.Get(), Colors.Warning), IconUtils.Others.None.Id, _ =>
                {
                    if (toggle != null)
                    {
                        toggle();
                        context.SaveConfig();
                        panel.Refresh();
                    }
                    else
                    {
                        EditSetting(context, player, setting);
                    }
                });
            }

            panel.NextButton("Modifier", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void EditSetting(BankPlugin context, Player player, Setting setting)
        {
            Panel panel = context.PanelHelper.Create($"{BankPlugin.Title} - {setting.Label}", UIPanel.PanelType.Input, player, () => EditSetting(context, player, setting));
            panel.TextLines.Add(setting.Label);
            panel.TextLines.Add($"Valeur actuelle : {setting.Get()}");
            panel.SetInputPlaceholder(setting.Get());

            panel.PreviousButtonWithAction("Valider", () =>
            {
                if (!setting.Set(panel.inputText))
                {
                    player.Notify(BankPlugin.Title, "Valeur invalide.", NotificationManager.Type.Error, 4f);
                    return Task.FromResult(false);
                }
                context.SaveConfig();
                player.Notify(BankPlugin.Title, $"{setting.Label} : {setting.Get()}", NotificationManager.Type.Success, 4f);
                return Task.FromResult(true);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void SetCardItem(BankPlugin context, Player player)
        {
            Panel panel = context.PanelHelper.Create($"{BankPlugin.Title} - Item carte", UIPanel.PanelType.Input, player, () => SetCardItem(context, player));
            panel.TextLines.Add($"Item actuel : {BankService.CardItemName(player)}");
            panel.TextLines.Add("Saisissez l'ID de l'item qui sert de carte bancaire (0 = aucune carte exigée)");
            panel.SetInputPlaceholder(Config.CardItemId.ToString());

            panel.PreviousButtonWithAction("Valider", () =>
            {
                if (!int.TryParse(panel.inputText?.Trim(), out int id) || id < 0 || (id > 0 && ItemUtils.GetItemById(id) == null))
                {
                    player.Notify(BankPlugin.Title, "ID d'item invalide.", NotificationManager.Type.Error, 4f);
                    return Task.FromResult(false);
                }
                Config.CardItemId = id;
                context.SaveConfig();
                player.Notify(BankPlugin.Title, $"Carte bancaire : {BankService.CardItemName(player)}", NotificationManager.Type.Success, 5f);
                return Task.FromResult(true);
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static void ConfirmRegenerateAll(BankPlugin context, Player player)
        {
            Panel panel = context.PanelHelper.Create($"{BankPlugin.Title} - Confirmation", UIPanel.PanelType.Text, player, () => ConfirmRegenerateAll(context, player));
            panel.TextLines.Add(Color("Tous les joueurs vont recevoir un nouveau code de carte bleue.", Colors.Warning));
            panel.TextLines.Add("Les joueurs connectés sont prévenus immédiatement, les autres verront leur code avec /macarte.");

            panel.PreviousButtonWithAction(Color("Confirmer", Colors.Error), async () =>
            {
                List<BankAccount> accounts = await BankAccount.QueryAll();
                foreach (BankAccount account in accounts)
                    await BankService.RegeneratePin(account, BankService.OnlinePlayer(account.CharacterId));
                player.Notify(BankPlugin.Title, $"{accounts.Count} code(s) régénéré(s).", NotificationManager.Type.Success, 5f);
                return true;
            });
            panel.PreviousButton("Annuler");
            panel.Display();
        }

        private static void PlayersMenu(BankPlugin context, Player player)
        {
            Panel panel = context.PanelHelper.Create($"{BankPlugin.Title} - Joueurs", UIPanel.PanelType.Tab, player, () => PlayersMenu(context, player));

            List<Player> players = Nova.server.Players.Where(p => p?.character != null).OrderBy(p => p.FullName).ToList();
            if (players.Count == 0)
                panel.AddTabLine("Aucun joueur connecté", _ => { });
            foreach (Player target in players)
                panel.AddTabLine(target.FullName, async _ => await PlayerMenu(context, player, target));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async Task PlayerMenu(BankPlugin context, Player player, Player target)
        {
            BankAccount account = await BankService.GetOrCreateAccount(target, true);
            if (account == null) return;

            Panel panel = context.PanelHelper.Create($"{BankPlugin.Title} - {target.FullName}", UIPanel.PanelType.Tab, player, async () => await PlayerMenu(context, player, target));
            panel.TextLines.Add($"Carte : {account.FormattedCardNumber}");
            panel.TextLines.Add($"Code : {Bold(account.Pin)}");
            panel.TextLines.Add($"Solde : {BankService.Money(target.Bank)} - liquide : {BankService.Money(target.Money)}");
            panel.TextLines.Add($"Frais payés : {BankService.Money(account.TotalFeesPaid)}");
            if (account.Opposed) panel.TextLines.Add(Color("Carte en opposition", Colors.Error));
            if (account.BlockedUntil > BankService.Now) panel.TextLines.Add(Color("Carte bloquée (erreurs de code)", Colors.Warning));

            panel.AddTabLine("Générer un nouveau code", async _ =>
            {
                await BankService.RegeneratePin(account, target);
                player.Notify(BankPlugin.Title, $"Nouveau code pour {target.FullName} : {account.Pin}", NotificationManager.Type.Success, 6f);
                panel.Refresh();
            });
            panel.AddTabLine("Débloquer la carte (opposition / erreurs)", async _ =>
            {
                account.Opposed = false;
                account.FailedAttempts = 0;
                account.BlockedUntil = 0;
                await account.Save();
                target.Notify(BankPlugin.Title, "Votre carte bancaire a été débloquée.", NotificationManager.Type.Success, 5f);
                panel.Refresh();
            });
            panel.AddTabLine("Donner une carte bancaire (item)", _ =>
            {
                if (Config.CardItemId <= 0)
                    player.Notify(BankPlugin.Title, "Configurez d'abord l'item carte bancaire.", NotificationManager.Type.Warning, 5f);
                else if (InventoryUtils.AddItem(target, Config.CardItemId, 1))
                {
                    player.Notify(BankPlugin.Title, $"Carte donnée à {target.FullName}.", NotificationManager.Type.Success, 4f);
                    target.Notify(BankPlugin.Title, "Vous avez reçu une carte bancaire.", NotificationManager.Type.Info, 4f);
                }
                else
                    player.Notify(BankPlugin.Title, "Inventaire du joueur plein.", NotificationManager.Type.Error, 4f);
            });
            panel.AddTabLine("Dernières opérations", async _ => await PlayerHistory(context, player, target, account));

            panel.NextButton("Sélectionner", () => panel.SelectTab());
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private static async Task PlayerHistory(BankPlugin context, Player player, Player target, BankAccount account)
        {
            List<BankTransaction> history = await BankService.History(account.CharacterId, 20);
            Panel panel = context.PanelHelper.Create($"{BankPlugin.Title} - Opérations {target.FullName}", UIPanel.PanelType.TabPrice, player, async () => await PlayerHistory(context, player, target, account));
            if (history.Count == 0)
                panel.AddTabLine("Aucune opération", _ => { });
            foreach (BankTransaction t in history)
                panel.AddTabLine($"{BankService.FormatDate(t.Date)} - {t.Type}", $"{BankService.Money(t.Amount)} / frais {BankService.Money(t.Fee)}", IconUtils.Others.None.Id, _ => { });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }
    }

    // ======================================================================
    //  Points bleus « DAB » (AAMenu > Administration > Points bleus)
    // ======================================================================

    /// <summary>
    /// Modèle de DAB. Un modèle peut être placé autant de fois que voulu :
    /// chaque placement crée un point bleu (NPoint) à la position du staff.
    /// </summary>
    public class AtmPattern : ModEntity<AtmPattern>, PatternData
    {
        [AutoIncrement]
        [PrimaryKey]
        public int Id { get; set; }

        public string PatternName { get; set; }

        [Ignore]
        public string TypeName { get; set; } = nameof(AtmPattern);

        [Ignore]
        public ModKit.ModKit Context { get; set; }

        public AtmPattern() { }

        public AtmPattern(bool isCreated)
        {
            TypeName = nameof(AtmPattern);
        }

        public void OnPlayerTrigger(Player player)
        {
            // Le même objet sert pour tous les points : on copie le nom tout de suite
            BankMenus.OpenAtm(Context, player, string.IsNullOrEmpty(PatternName) ? "DAB" : PatternName);
        }

        public async Task SetProperties(int id)
        {
            AtmPattern result = await Query(id);
            Id = id;
            TypeName = nameof(AtmPattern);
            PatternName = result?.PatternName;
        }

        /// <summary>Menu staff : choisir un modèle et placer un DAB à sa position.</summary>
        public async void CreateOrGenerate(Player player)
        {
            if (!player.IsAdmin) return;

            List<AtmPattern> patterns = await QueryAll();
            Panel panel = Context.PanelHelper.Create("DAB - Placer un DAB", UIPanel.PanelType.Tab, player, () => CreateOrGenerate(player));

            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle, créez-en un", _ => { });
            foreach (AtmPattern pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}", async _ =>
                {
                    pattern.TypeName = nameof(AtmPattern);
                    pattern.Context = Context;
                    if (await Context.PointHelper.CreateNPoint(player, pattern))
                        player.Notify("DAB", $"DAB « {pattern.PatternName} » placé à votre position.", NotificationManager.Type.Success, 5f);
                    else
                        player.Notify("DAB", "Erreur lors de la création du point.", NotificationManager.Type.Error, 5f);
                });
            }

            if (patterns.Count > 0)
                panel.NextButton("Placer ici", () => panel.SelectTab());
            panel.NextButton("Nouveau modèle", () => SetPatternData(player));
            panel.NextButton("Modèles", async () => await GetPatternData(player, true));
            panel.NextButton("Points", async () => await GetNPoints(player));
            panel.CloseButton();
            panel.Display();
        }

        public void SetPatternData(Player player)
        {
            Panel panel = Context.PanelHelper.Create("DAB - Nouveau modèle", UIPanel.PanelType.Input, player, () => SetPatternData(player));
            panel.TextLines.Add("Nom du DAB (affiché aux joueurs)");
            panel.SetInputPlaceholder("DAB Banque Centrale");

            panel.PreviousButtonWithAction("Créer", async () =>
            {
                string name = panel.inputText?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    player.Notify("DAB", "Le nom ne peut pas être vide.", NotificationManager.Type.Error, 5f);
                    return false;
                }

                AtmPattern pattern = new AtmPattern(false) { PatternName = name };
                if (!await pattern.Save())
                {
                    player.Notify("DAB", "Erreur lors de l'enregistrement.", NotificationManager.Type.Error, 5f);
                    return false;
                }

                player.Notify("DAB", $"Modèle « {name} » créé. Choisissez-le puis « Placer ici ».", NotificationManager.Type.Success, 5f);
                return true;
            });
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        public async Task GetPatternData(Player player, bool forEdit)
        {
            List<AtmPattern> patterns = await QueryAll();
            string action = "";

            Panel panel = Context.PanelHelper.Create("DAB - Modèles", UIPanel.PanelType.Tab, player, async () => await GetPatternData(player, forEdit));

            if (patterns.Count == 0)
                panel.AddTabLine("Aucun modèle", _ => { });
            foreach (AtmPattern pattern in patterns)
            {
                panel.AddTabLine($"[{pattern.Id}] {pattern.PatternName}", async _ =>
                {
                    pattern.TypeName = nameof(AtmPattern);
                    pattern.Context = Context;

                    if (action == "rename")
                    {
                        RenamePattern(player, pattern);
                    }
                    else if (action == "delete")
                    {
                        await Context.PointHelper.DeleteNPointsByPattern(player, pattern);
                        if (await pattern.Delete())
                            player.Notify("DAB", $"Modèle « {pattern.PatternName} » et ses DAB supprimés.", NotificationManager.Type.Success, 5f);
                        else
                            player.Notify("DAB", "Erreur lors de la suppression.", NotificationManager.Type.Error, 5f);
                        panel.Refresh();
                    }
                });
            }

            if (patterns.Count > 0 && forEdit)
            {
                panel.NextButton("Renommer", () => { action = "rename"; panel.SelectTab(); });
                panel.AddButton(Color("Supprimer", Colors.Error), _ => { action = "delete"; panel.SelectTab(); });
            }
            panel.PreviousButton();
            panel.CloseButton();
            panel.Display();
        }

        private void RenamePattern(Player player, AtmPattern pattern)
        {
            Panel panel = Context.PanelHelper.Create("DAB - Renommer", UIPanel.PanelType.Input, player, () => RenamePattern(player, pattern));
            panel.TextLines.Add($"Nouveau nom pour « {pattern.PatternName} »");
            panel.SetInputPlaceholder(pattern.PatternName ?? "DAB");

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

        /// <summary>Liste de tous les DAB placés : se téléporter, déplacer ou supprimer.</summary>
        public async Task GetNPoints(Player player)
        {
            List<NPoint> points = await ModEntity<NPoint>.Query(p => p.TypeName == nameof(AtmPattern));
            Dictionary<int, string> names = (await QueryAll()).ToDictionary(p => p.Id, p => p.PatternName);
            string action = "";

            Panel panel = Context.PanelHelper.Create("DAB - Points placés", UIPanel.PanelType.Tab, player, async () => await GetNPoints(player));

            if (points.Count == 0)
                panel.AddTabLine("Aucun DAB placé", _ => { });
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
                                player.Notify("DAB", "DAB déplacé à votre position.", NotificationManager.Type.Success, 5f);
                            break;
                        case "delete":
                            await Context.PointHelper.DeleteNPoint(point);
                            player.Notify("DAB", $"DAB #{point.Id} supprimé.", NotificationManager.Type.Success, 5f);
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
