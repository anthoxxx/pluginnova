# Boulangerie — by Loriss Strange

Plugin Nova-Life qui crée le **métier de boulanger** : une **caisse enregistreuse** (point
bleu) dans la boulangerie, une carte de **produits** et de **formules maison** entièrement
configurable depuis AAMenu, un **paiement en espèces ou par carte** par le client, et un
journal des ventes sur **Discord** (webhook).

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) installés sur le serveur.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans le dossier `Boulangerie/libs/`.
2. Ouvrir **`Boulangerie.csproj`** directement (Fichier → Ouvrir → Projet/Solution).
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/Boulangerie.dll` dans le dossier `Plugins` du serveur.

## Mise en place (staff, en service admin)
1. **Créer la caisse** : AAMenu → **Administration → Points → Caisse boulangerie**
   (ou `/caisses`) → **Nouveau modèle** → nom (ex. « Caisse comptoir ») → choisir
   l'**entreprise** de la boulangerie.
2. Choisir le modèle puis **Placer ici** : un point bleu apparaît à votre position.
   Vous pouvez en placer plusieurs (plusieurs caisses dans la même boulangerie).
3. **Configurer le plugin** : AAMenu → **Administration → Plugins → Boulangerie**
   (ou `/boulangerie`).

Dans **Modèles** : renommer une caisse, changer son entreprise, lui donner un **webhook
Discord à elle** (sinon celui de la configuration), ou la supprimer.
Dans **Points** : se téléporter, déplacer ou supprimer une caisse placée.

## Configuration (Administration → Plugins → Boulangerie)
| Réglage | Par défaut | Rôle |
|---|---|---|
| Webhook Discord (URL) | — | Salon où chaque vente est envoyée |
| Webhook activé | Non | Active l'envoi (aussi pour les webhooks propres aux caisses) |
| Couleur des embeds | `#E8A33D` | Couleur des messages Discord |
| Tester le webhook | | Envoie un message de test |
| Distance max avec le client | 6 m | Rayon pour trouver le client à encaisser |
| Commission du boulanger | 0 % | Part de la vente versée en banque au boulanger qui encaisse, le reste va dans le compte de l'entreprise |
| Remise maximale | 30 % | Remise maximale qu'un boulanger peut faire sur un panier (0 = pas de remise) |
| Délai de paiement | 90 s | Temps laissé au client pour payer |
| Prise de service obligatoire | Oui | Il faut être en service pour utiliser la caisse |
| Donner les items au client | Oui | Le client reçoit les items liés aux produits |
| Prendre les items dans l'inventaire du boulanger | Non | Le boulanger doit avoir les produits sur lui (stock réel) |
| Les employés peuvent modifier la carte | Non | Sinon seul le patron (et le staff) |
| Gérer la carte d'une boulangerie | | Le staff peut modifier la carte de n'importe quelle boulangerie |

## La carte (patron)
AAMenu → **Métier → Boulangerie**, ou à la caisse → **Gérer la boulangerie** :
- **Produits** : *Ajouter* → nom → prix → item du jeu remis au client (choisi dans votre
  inventaire, par ID, ou aucun). *Modifier* : nom, prix, item, **en vente / rupture**, supprimer.
- **Formules maison** : *Ajouter* → nom (ex. « Petit-déjeuner ») → prix → contenu
  (+1 / −1 sur chaque produit, ex. 1 croissant + 1 café). Le prix « à l'unité » est affiché
  pour comparer.
- **Statistiques** : chiffre d'affaires du jour / 7 jours / 30 jours / total, espèces vs carte,
  meilleurs vendeurs, articles les plus vendus.
- **Historique des ventes** : les 50 derniers tickets, avec le détail de chacun.

## À la caisse (boulanger)
1. Le client demande un croissant : **+ Ajouter un produit** → *Croissant* (ou *Quantité...*).
   Pour une formule : **+ Ajouter une formule** → *Petit-déjeuner*.
2. Le panier s'affiche avec le total. Sélectionner une ligne retire 1 article.
   **Appliquer une remise** si besoin, **Vider le panier** pour recommencer.
3. Quand le client a fini : **Encaisser le client** → choisir le client à proximité →
   **Envoyer l'addition**.
4. Le client reçoit un **récapitulatif** (articles, remise, total, son argent liquide et son
   solde bancaire) et choisit **Espèces**, **Carte bancaire** ou **Refuser**.
5. Paiement accepté : l'argent va dans le compte de l'entreprise (moins la commission), le
   client reçoit ses items, le ticket est enregistré et envoyé sur Discord.

Tant que le client n'a pas payé, la caisse affiche « En attente du paiement » (sélectionner
pour annuler). « Mes ventes du jour » indique le nombre et le total de vos ventes.

Les données sont dans la base ModKit (`Plugins/ModKit/data.sqlite`).
