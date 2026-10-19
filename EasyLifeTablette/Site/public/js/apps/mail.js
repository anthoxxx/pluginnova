// Application Mail
import { api, h, clear, icon, ago, dateTimeFr, attempt, modal, form, confirmBox, empty, loading, sideLayout } from '../core.js';

export default {
  id: 'mail',
  name: 'Mail',
  icon: 'mail',
  color: 'linear-gradient(180deg,#3ea2ff,#0a6cff)',
  keywords: 'email message courrier',
  open(root, ctx) {
    let accounts = [];
    let domain = ctx.me.mailDomain;
    let current = null;
    let layout = null;

    async function load(selectId) {
      clear(root, loading());
      const data = await attempt(() => api('mail/comptes'));
      if (!data) return;
      accounts = data.accounts;
      domain = data.domain;
      if (!accounts.length) return welcome();
      current = accounts.find((a) => a.id === selectId) || accounts.find((a) => a.id === current?.id) || accounts[0];
      layout = sideLayout(root, {
        appName: 'Mail',
        subtitle: current.address,
        iconName: 'mail',
        color: '#0a84ff',
        headerAction: h('button', { class: 'btn btn-primary block', onclick: () => compose() }, icon('pencil', 14), 'Écrire'),
        sections: () => [
          { items: [
            { id: 'inbox', label: 'Boîte de réception', icon: 'inbox', badge: current.unread || null },
            { id: 'sent', label: 'Envoyés', icon: 'send' },
          ] },
          { title: 'Mes adresses', items: [
            ...accounts.map((a) => ({ id: `acc-${a.id}`, label: a.address, icon: a.id === current.id ? 'check' : 'user', badge: a.unread || null })),
            { id: 'add', label: 'Ajouter une adresse', icon: 'plus' },
          ] },
        ],
        onSelect: (id) => {
          if (id.startsWith('acc-')) return load(Number(id.slice(4)));
          if (id === 'add') return addAddress();
          return folder(id);
        },
      });
      layout.select('inbox');
    }

    function welcome() {
      const feature = (ic, title, text) => h('div', { class: 'feature' }, icon(ic, 18), h('div', {}, h('strong', {}, title), h('p', {}, text)));
      clear(root, h('div', { class: 'welcome' },
        h('div', { class: 'app-icon big', style: { background: '#0a84ff' } }, icon('mail', 40)),
        h('h1', {}, 'Bienvenue dans Mail'),
        h('p', { class: 'muted' }, `Créez votre adresse @${domain} ou connectez-vous à une adresse existante (adresse professionnelle remise par votre employeur, par exemple).`),
        h('div', { class: 'row center gap' },
          h('button', { class: 'btn btn-primary', onclick: createAddress }, icon('plus', 14), 'Créer une adresse'),
          h('button', { class: 'btn', onclick: loginAddress }, icon('logout', 14), 'Se connecter')),
        h('div', { class: 'features' },
          feature('users', 'Plusieurs adresses', 'Personnelle, professionnelle, boîtes partagées : passez de l\'une à l\'autre en un geste.'),
          feature('bell', 'Notifications', 'Chaque nouveau message vous prévient sur la tablette et à l\'écran en ville.'),
          feature('tablet', 'Partout', 'Lisez vos mails en jeu sur l\'ordinateur ou depuis votre navigateur.'),
          feature('lock', 'Sécurisé', 'Chaque adresse est protégée par un mot de passe que vous choisissez.'))));
    }

    function addAddress() {
      modal('Ajouter une adresse', (close) => h('div', { class: 'row center gap' },
        h('button', { class: 'btn btn-primary', onclick: () => { close(); createAddress(); } }, 'Créer une adresse'),
        h('button', { class: 'btn', onclick: () => { close(); loginAddress(); } }, 'Se connecter à une adresse')));
    }

    function createAddress() {
      modal('Créer une adresse', (close) => form([
        { name: 'local', label: `Adresse (avant @${domain})`, placeholder: 'prenom.nom', required: true, max: 30 },
        { name: 'displayName', label: 'Nom affiché', value: ctx.me.rpName || ctx.me.pseudo, required: true, max: 60 },
        { name: 'password', label: 'Mot de passe (6 caractères min.)', type: 'password', required: true, autocomplete: 'new-password' },
      ], 'Créer', async (v) => {
        const r = await attempt(() => api('mail/comptes', { method: 'POST', body: v }), 'Adresse créée !');
        if (r) { close(); load(r.id); }
      }));
    }

    function loginAddress() {
      modal('Se connecter à une adresse', (close) => form([
        { name: 'address', label: 'Adresse', placeholder: `contact@${domain}`, required: true },
        { name: 'password', label: 'Mot de passe', type: 'password', required: true },
      ], 'Se connecter', async (v) => {
        const r = await attempt(() => api('mail/connexion', { method: 'POST', body: v }), 'Adresse ajoutée à votre tablette.');
        if (r) { close(); load(r.id); }
      }));
    }

    async function folder(name) {
      layout.setTitle(name === 'inbox' ? 'Boîte de réception' : 'Envoyés');
      layout.setActions(
        h('button', { class: 'icon-btn', title: 'Actualiser', onclick: () => folder(name) }, icon('refresh', 16)),
        h('button', { class: 'icon-btn', title: 'Gérer l\'adresse', onclick: manageAccount }, icon('settings', 16)));
      clear(layout.content, loading());
      const data = await attempt(() => api(`mail/comptes/${current.id}/${name === 'inbox' ? 'reception' : 'envoyes'}`));
      if (!data) return;
      if (!data.messages.length) return clear(layout.content, empty('inbox', 'Aucun message', name === 'inbox' ? 'Les mails reçus apparaîtront ici.' : 'Les mails envoyés apparaîtront ici.'));
      clear(layout.content, h('div', { class: 'list' }, data.messages.map((m) => h('button', {
        class: `list-row ${m.read ? '' : 'unread'}`,
        onclick: () => read(m.id, name),
      },
        h('div', { class: 'avatar' }, (name === 'inbox' ? m.from_name : m.to_addrs)[0]?.toUpperCase() || '?'),
        h('div', { class: 'grow ellipsis' },
          h('div', { class: 'row between' }, h('strong', { class: 'ellipsis' }, name === 'inbox' ? m.from_name : `À : ${m.to_addrs}`), h('small', { class: 'muted nowrap' }, ago(m.created_at))),
          h('div', { class: 'ellipsis' }, m.subject),
          h('small', { class: 'muted ellipsis' }, m.preview.replace(/\s+/g, ' ')))))));
    }

    async function read(id, from) {
      const data = await attempt(() => api(`mail/messages/${id}`));
      if (!data) return;
      const m = data.message;
      if (from === 'inbox' && current.unread) { current.unread -= 1; layout.refreshNav(); }
      layout.setTitle(m.subject);
      layout.setActions(
        h('button', { class: 'icon-btn', title: 'Retour', onclick: () => folder(from) }, icon('back', 16)),
        h('button', { class: 'btn', onclick: () => compose({ to: m.folder === 'inbox' ? m.from_addr : m.to_addrs, subject: m.subject.startsWith('RE :') ? m.subject : `RE : ${m.subject}`, quote: m }) }, icon('send', 14), 'Répondre'),
        h('button', { class: 'icon-btn danger', title: 'Supprimer', onclick: async () => {
          if (!await confirmBox('Supprimer ce message ?', 'Supprimer')) return;
          if (await attempt(() => api(`mail/messages/${id}`, { method: 'DELETE', body: {} }), 'Message supprimé.')) folder(from);
        } }, icon('trash', 16)));
      clear(layout.content, h('article', { class: 'mail-view' },
        h('div', { class: 'mail-meta' },
          h('div', { class: 'avatar big' }, m.from_name[0]?.toUpperCase() || '?'),
          h('div', {}, h('strong', {}, m.from_name), h('div', { class: 'muted small' }, `<${m.from_addr}>`), h('div', { class: 'muted small' }, `À : ${m.to_addrs}`)),
          h('small', { class: 'muted push' }, dateTimeFr(m.created_at))),
        h('div', { class: 'mail-body' }, m.body)));
    }

    function compose(prefill = {}) {
      const body = prefill.quote ? `\n\n----- Le ${dateTimeFr(prefill.quote.created_at)}, ${prefill.quote.from_name} a écrit :\n${prefill.quote.body}` : '';
      modal(`Nouveau message — ${current.address}`, (close) => form([
        { name: 'to', label: 'À (séparez plusieurs adresses par une virgule)', value: prefill.to || '', placeholder: `exemple@${domain}`, required: true },
        { name: 'subject', label: 'Objet', value: prefill.subject || '', max: 150 },
        { name: 'body', label: 'Message', type: 'textarea', rows: 10, value: body, required: true, max: 5000 },
      ], 'Envoyer', async (v) => {
        if (await attempt(() => api(`mail/comptes/${current.id}/envoyer`, { method: 'POST', body: v }), 'Message envoyé.')) {
          close();
          if (layout) layout.select('sent');
        }
      }), { wide: true });
    }

    function manageAccount() {
      modal(current.address, (close) => h('div', { class: 'stack' },
        current.owner ? form([
          { name: 'password', label: 'Nouveau mot de passe', type: 'password', required: true, autocomplete: 'new-password', hint: 'Les autres personnes connectées à cette adresse devront se reconnecter.' },
        ], 'Changer le mot de passe', async (v) => {
          if (await attempt(() => api(`mail/comptes/${current.id}/mot-de-passe`, { method: 'PUT', body: v }), 'Mot de passe modifié.')) close();
        }) : null,
        h('button', { class: 'btn', onclick: async () => {
          if (!await confirmBox(`Retirer ${current.address} de votre tablette ? Vous pourrez vous reconnecter avec le mot de passe.`, 'Retirer')) return;
          if (await attempt(() => api(`mail/comptes/${current.id}`, { method: 'DELETE', body: {} }), 'Adresse retirée.')) { close(); current = null; load(); }
        } }, 'Retirer de ma tablette'),
        current.owner ? h('button', { class: 'btn btn-danger', onclick: async () => {
          if (!await confirmBox(`Supprimer définitivement ${current.address} et tous ses messages ?`, 'Supprimer')) return;
          if (await attempt(() => api(`mail/comptes/${current.id}?definitif=1`, { method: 'DELETE', body: {} }), 'Adresse supprimée.')) { close(); current = null; load(); }
        } }, 'Supprimer l\'adresse') : null));
    }

    load();
  },
};
