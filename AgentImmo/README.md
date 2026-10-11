# Agent Immo — by Loris Strange

Plugin Nova-Life : un **vrai métier d'agent immobilier**. Les agences vendent et louent les
terrains (ceux de l'État et ceux des joueurs), touchent des commissions, gèrent les baux et
les loyers sont prélevés automatiquement. Tout se fait dans **AAMenu**.

## Dépendances
- ModKit (`ModKit.dll`) et AAMenu (`AAMenu.dll`) installés sur le serveur.

## Compilation (Visual Studio)
1. Copier les 7 DLL listées dans `libs/README.md` dans le dossier `AgentImmo/libs/`.
2. Ouvrir **`AgentImmo.csproj`** directement (Fichier → Ouvrir → Projet/Solution).
3. Générer en **Release** (ou `dotnet build -c Release`).
4. Copier `bin/Release/net472/AgentImmo.dll` dans le dossier `Plugins` du serveur.

## Mise en place (staff)
Être admin **et en service admin**, puis AAMenu → **Administration → Agent Immo** :
1. **Agences immobilières (sociétés)** : cliquer sur une société pour en faire une agence
   (marquée `[Agence]`). Toutes les sociétés sont proposées, quel que soit leur type.
2. **Paramètres** (enregistrés dans `Plugins/AgentImmo/config.json`) :

| Paramètre | Défaut | Rôle |
|---|---|---|
| Commission sur les biens des joueurs | 10 % | Part de l'agence sur chaque vente / loyer d'un terrain de joueur |
| Part agence sur les terrains de l'État | 25 % | Part gardée par l'agence, le reste va à l'État |
| Prime de l'agent | 20 % | Part de la commission versée directement à l'agent qui conclut |
| Délai avant expulsion | 48 h | Temps laissé à un locataire en impayé |
| Durée de location par défaut | 7 j | Période proposée à la création d'une location |
| Locations max par joueur | 3 | 0 = illimité |
| Distance agent / client | 5 m | Distance max pour proposer un contrat |
| Terrains de l'État | Oui | Les agences peuvent vendre / louer les terrains libres |
| Service obligatoire | Non | L'agent doit être en service pour utiliser le menu |

Le staff voit aussi **toutes les annonces** (retirer) et **toutes les locations** (GPS, résilier).

> Astuce : pour que les joueurs passent obligatoirement par une agence, mettez à 0 le prix de
> location / vente « direct » des terrains du jeu.

## Côté agence — AAMenu → **Métier → Agence immobilière**
(visible uniquement pour les employés d'une société déclarée agence)

- **Catalogue des biens** : chaque annonce → proposer la vente / location au client à côté
  de vous, GPS vers le terrain, envoyer le GPS au client, modifier prix / durée / description,
  retirer l'annonce.
- **Nouveau bien : terrain de l'État** : numéro du terrain (pré-rempli si vous êtes dessus) →
  vente ou location → prix (prix de référence du jeu proposé) → publication.
- **Nouveau mandat : terrain d'un client** : le propriétaire est à côté de vous → choix d'un de
  ses terrains → vente ou location → prix → il reçoit le mandat et le signe (ou refuse).
- **Locations en cours** : locataire, échéance, impayés, GPS, résiliation / expulsion.
- **Demandes clients** : demandes envoyées depuis les annonces → GPS, prévenir le client,
  marquer comme traitée.
- **Terrain où je me trouve**, **Historique des opérations**, **Comptes de l'agence**
  (commissions, solde de la société pour le patron).

### Déroulement d'une vente
L'agent ouvre l'annonce → *Proposer la vente au client proche* → le client voit l'offre
(prix, propriétaire, description, son solde) → **Acheter** / Visiter (GPS) / Refuser.
À l'achat : le client est débité (banque), devient propriétaire du terrain, le vendeur est payé
(moins la commission), l'agence et l'agent touchent leur part.

### Déroulement d'une location
Même principe avec **Signer le bail** : le premier loyer est payé tout de suite.
- Terrain de l'État : le locataire en devient l'occupant pendant le bail.
- Terrain d'un joueur : le propriétaire le reste, le locataire est ajouté en **co-propriétaire**.

Ensuite le loyer est **prélevé automatiquement** à chaque échéance. En cas de fonds
insuffisants, le locataire est prévenu chaque heure puis **expulsé** après le délai configuré.
À la fin d'un bail, le terrain est rendu et l'annonce repasse en ligne automatiquement.

## Côté joueurs — AAMenu → **Interactions → Agence immobilière**
- **Annonces immobilières / de location** : voir les biens, GPS, contacter l'agence.
- **Mes locations** : échéance, GPS, payer une période d'avance, résilier son bail.
- **Mes biens en agence** : retirer un mandat.
- **Mes biens loués** : suivi des locataires et des loyers.
- **Agences immobilières** : liste des agences, agents en ligne, GPS.

Un joueur hors ligne qui doit recevoir de l'argent (vente ou loyer de son terrain) le reçoit
automatiquement à sa prochaine connexion.

## Données
Base ModKit (`Plugins/ModKit/data.sqlite`) : tables `ImmoListing` (annonces), `ImmoLease`
(baux), `ImmoLog` (historique), `ImmoPayout` (paiements en attente), `ImmoRequest` (demandes).
