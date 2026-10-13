# EXO By Loris Strange

Plugin Nova-Life : **les stations-service sont bloquées**. Seules les entreprises que vous
autorisez peuvent servir de l'essence, et ce sont elles qui font payer les clients, au prix
que vous avez choisi. Quand aucun employé n'est en service, les joueurs paient d'avance à un
**automate** (point bleu) qui débloque la pompe.

## Fonctionnement
- Si quelqu'un qui ne fait pas partie du personnel branche un pistolet, il est raccroché
  tout de suite, l'essence est retirée et un message s'affiche : « Seul le personnel de X
  peut servir de l'essence ici ».
- Les pompes qui ne sont pas configurées sont toujours bloquées.
- **Prise de service** : les employés prennent leur service dans **Métier → Station-service**.
  Seul un employé **en service** (à moins de 4 m de la pompe) peut servir de l'essence.
- L'employé branche le pistolet sur la voiture du client comme d'habitude. Quand il
  raccroche, le plugin lui propose les joueurs proches : il choisit le client, qui reçoit une
  **facture** (payer en espèces, payer par carte, ou refuser).
- L'argent va sur le **compte de l'entreprise**, moins la **prime de l'employé** (en %).
- Le jeu ne fait plus payer l'essence lui-même : le plugin met le prix du jeu à 0 et
  affiche votre prix sur les panneaux des stations.
- Les **jerricans** remplis à la pompe sont soumis aux mêmes règles (employé en service
  uniquement, 15 L à facturer).

## Automate (quand personne n'est en service)
Le staff place un **point bleu « Automate »** dans chaque station. Quand **aucun employé de
la station n'est en service**, un joueur qui entre dans le point bleu voit :
1. la liste des pompes de la station (**Pompe 1, Pompe 2, Pompe 3…**) avec leur état
   (libre, occupée, fermée, citerne vide) ;
2. les montants : **10, 20, 30, 40, 50, 60, 70, 80 €** (modifiables par le staff), avec le
   nombre de litres correspondant ;
3. le paiement : **espèces** ou **carte**.

La pompe choisie est alors **débloquée pour ce joueur pendant 5 minutes** (GPS vers la pompe).
Il branche le pistolet lui-même ; **la pompe se bloque dès que le montant est atteint**.
S'il met moins que prévu, la différence lui est **rendue**. S'il ne se sert pas dans les
5 minutes, il est **remboursé** entièrement. Il peut aussi annuler en repassant dans le point bleu.

Si un employé est en service, l'automate répond : « Adressez-vous au personnel de la station ».
L'argent de l'automate va sur le compte de l'entreprise de la pompe (sans prime).

## Webhook Discord (staff)
Toutes les actions importantes sont envoyées sur un salon Discord :
prises et fins de service, ventes (pompe, litres, montant, client, employé, prime),
factures refusées, pleins offerts, automate (paiement, plein terminé, remboursement, annulation),
commandes de carburant, changements de configuration.

Configuration : **Configuration → Webhook Discord** (coller l'URL), ou la commande
**`/exowebhook <url>`** (`/exowebhook 0` pour le retirer). « Tester le webhook » envoie un message de test.

## Configuration (staff, en service admin)
AAMenu → **Administration → Plugins → EXO By Loris Strange** (ou `/exo`) :

| Menu | Rôle |
|---|---|
| Entreprises autorisées | cocher les entreprises qui peuvent gérer les stations |
| Pompes / Ajouter la pompe la plus proche / Ajouter toutes les pompes | configurer les pompes (nom, station, entreprise, prix, citerne, ouverte/fermée) |
| Prix du litre, Prime employé | prix de vente global, % reversé à l'employé |
| Gestion du stock | citernes, prix d'achat du litre, capacité |
| Automate | activer/désactiver, montants proposés |
| Automates (points bleus) | créer un automate pour une station et placer son point bleu |
| Webhook Discord | URL du webhook, test |
| Toutes les ventes | historique |

**Placer un automate** : Automates (points bleus) → **Nouvel automate** → choisir la station →
choisir l'automate → se placer à l'endroit voulu → **Placer ici**. Aussi disponible dans
AAMenu → **Administration → Points bleus → Automate essence**.

## Utilisation (employés)
AAMenu → **Métier → Station-service** (la ligne n'apparaît que pour les entreprises autorisées) :
- **Prendre / terminer mon service**, et nombre de collègues en service.
- **Pompes** : ouvrir/fermer, prix, citerne, commander du carburant (patron), GPS.
- **Pleins à facturer** : pleins pas encore payés. « Offrir » supprime la facture.
- **Ventes récentes** de l'entreprise.

## Compilation
1. Copier les 6 DLL listées dans `libs/README.md` dans `Exo/libs/`.
2. Ouvrir `Exo.csproj` puis générer en **Release** (ou `dotnet build -c Release`).
3. Copier `bin/Release/net472/Exo.dll` dans le dossier `Plugins` du serveur (ModKit et AAMenu
   doivent être installés). **Supprimez l'ancien `Gaz.dll`** s'il est encore là.

Les données sont enregistrées dans la base ModKit (tables `GazConfig`, `GazPump`, `GazSale`,
`ExoAutomatePattern`) : la configuration faite avec l'ancienne version « Gaz » est conservée.

## À savoir
- Le plugin met `fuelPrice` à 0 dans la config du serveur (en mémoire). Si vous retirez le
  plugin, vérifiez le prix de l'essence dans la config du serveur.
- Les factures en attente, les prises de service et les paiements d'automate en cours sont
  gardés en mémoire : ils sont perdus au redémarrage du serveur (un paiement d'automate en
  cours n'est alors pas remboursé). Une reconnexion termine le service.
- AAMenu filtre le menu Métier par **activité**, pas par entreprise. La ligne « Station-service »
  est donc affichée pour les activités des entreprises autorisées (ou leur activité personnalisée
  ModKit). Une autre entreprise qui a exactement la même activité la voit aussi, mais elle ne peut
  pas l'ouvrir. Une entreprise autorisée sans activité passe par `/exo`.
