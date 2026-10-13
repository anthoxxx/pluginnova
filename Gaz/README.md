# Gaz By Loris Strange

Plugin Nova-Life : **les stations-service sont bloquées**. Seules les entreprises que vous
autorisez peuvent servir de l'essence, et ce sont elles qui font payer les clients, au prix
que vous avez choisi.

## Fonctionnement
- Si quelqu'un qui ne fait pas partie du personnel branche un pistolet, il est raccroché
  tout de suite, l'essence est retirée et un message s'affiche : « Seul le personnel de X
  peut servir de l'essence ici ».
- Les pompes qui ne sont pas configurées sont toujours bloquées.
- Un employé d'une entreprise autorisée (à moins de 4 m de la pompe) branche le pistolet
  sur la voiture du client comme d'habitude. Quand il raccroche, le plugin lui propose les
  joueurs proches : il choisit le client, qui reçoit une **facture** (payer en espèces,
  payer par carte, ou refuser).
- L'argent va sur le **compte de l'entreprise**, moins la **prime de l'employé** (en %).
- Le jeu ne fait plus payer l'essence lui-même : le plugin met le prix du jeu à 0 et
  affiche votre prix sur les panneaux des stations.
- Les **jerricans** remplis à la pompe sont soumis aux mêmes règles. Un employé qui remplit
  un jerrican a un plein de 15 L à facturer ; pour les autres joueurs, le remplissage est
  annulé.

### Ce que j'ai ajouté en plus
- **Citernes / stock** (désactivable) : chaque pompe a une citerne. Le patron achète le
  carburant avec l'argent de l'entreprise, au prix d'achat défini par le staff. Quand la
  citerne est vide, la pompe est bloquée.
- **Ouvrir / fermer** une pompe depuis le menu Métier.
- **Prix par pompe** (sinon c'est le prix global qui s'applique).
- **Prime employé** en % de chaque vente.
- **Statistiques** par pompe (litres vendus, chiffre d'affaires) et **historique des ventes**.
- **Stations** : les pompes sont regroupées par station (« Station du port - Pompe 1 », etc.),
  et toutes les pompes de la carte peuvent être détectées automatiquement.
- **GPS** vers une pompe, **téléportation** pour le staff.
- Commande de secours **`/gaz`** si AAMenu n'est pas installé.

## Configuration (staff, en service admin)
AAMenu → **Administration → Plugins → Gaz By Loris Strange** (ou bien
**Métier → Station-service → Configuration**, ou bien `/gaz`) :

| Menu | Rôle |
|---|---|
| Entreprises autorisées | cocher les entreprises qui peuvent gérer les stations |
| Ajouter la pompe la plus proche | se placer à côté d'une pompe du jeu, puis lui donner un nom (Pompe 1…) |
| Ajouter toutes les pompes de la carte | ajoute toutes les pompes d'un coup (regroupées en Station 1, Station 2…) |
| Pompes | pour chaque pompe : nom, station, entreprise (une seule ou toutes), prix, citerne, ouverte/fermée, téléportation, suppression |
| Prix du litre | prix de vente global |
| Prime employé | % de chaque vente reversé à l'employé |
| Gestion du stock | activer/désactiver les citernes, prix d'achat du litre, capacité |
| Toutes les ventes | historique |

Avec la gestion du stock activée, une nouvelle pompe a une citerne **vide** : le patron
doit commander du carburant (ou le staff peut remplir la citerne depuis le menu de la pompe).

## Utilisation (employés)
AAMenu → **Métier → Station-service** (la ligne n'apparaît que pour les entreprises autorisées) :
- **Pompes** : ouvrir/fermer, voir le prix et la citerne, commander du carburant (patron), GPS.
- **Pleins à facturer** : pleins pas encore payés (si vous avez fermé la fenêtre, par exemple).
  « Offrir » supprime la facture.
- **Ventes récentes** de l'entreprise.

## Compilation
1. Copier les 6 DLL listées dans `libs/README.md` dans `Gaz/libs/`.
2. Ouvrir `Gaz.csproj` puis générer en **Release** (ou `dotnet build -c Release`).
3. Copier `bin/Release/net472/Gaz.dll` dans le dossier `Plugins` du serveur (ModKit et AAMenu
   doivent être installés).

Les données sont enregistrées dans la base ModKit (tables `GazConfig`, `GazPump`, `GazSale`).

## À savoir
- Le plugin met `fuelPrice` à 0 dans la config du serveur (en mémoire). Si vous retirez le
  plugin, vérifiez le prix de l'essence dans la config du serveur.
- Les factures en attente sont gardées en mémoire : elles sont perdues au redémarrage du serveur.
- AAMenu filtre le menu Métier par **activité**, pas par entreprise. La ligne « Station-service »
  est donc affichée pour les activités des entreprises autorisées (ou leur activité personnalisée
  ModKit). Une autre entreprise qui a exactement la même activité la voit aussi, mais elle ne peut
  pas l'ouvrir. Une entreprise autorisée sans activité passe par `/gaz`.
