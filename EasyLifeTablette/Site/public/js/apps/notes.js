// Application Notes : bloc-notes personnel, enregistrement automatique
import { api, h, clear, icon, ago, attempt, confirmBox, empty, loading } from '../core.js';

export default {
  id: 'notes',
  name: 'Notes',
  icon: 'feather',
  color: 'linear-gradient(180deg,#5ac8fa,#0a84ff)',
  keywords: 'bloc note memo',
  open(root) {
    let notes = [];
    let current = null;
    let saveTimer = null;
    const list = h('div', { class: 'notes-list' });
    const editor = h('div', { class: 'note-editor' });

    clear(root, h('div', { class: 'split' },
      h('aside', { class: 'side' },
        h('div', { class: 'side-head' }, h('div', { class: 'app-icon small', style: { background: '#0a84ff' } }, icon('feather', 18)), h('div', {}, h('strong', {}, 'Notes'))),
        h('button', { class: 'btn btn-primary block', onclick: create }, icon('plus', 14), 'Nouvelle note'),
        list),
      h('section', { class: 'pane' }, editor)));

    async function load(selectId) {
      clear(list, loading());
      const data = await attempt(() => api('notes'));
      if (!data) return;
      notes = data.notes;
      drawList();
      const target = notes.find((n) => n.id === selectId) || notes[0];
      if (target) open(target);
      else clear(editor, empty('feather', 'Aucune note', 'Créez une note pour garder une adresse, un numéro, une idée…'));
    }

    function drawList() {
      clear(list, notes.map((n) => h('button', { class: `side-item note-item ${current?.id === n.id ? 'active' : ''}`, onclick: () => open(n) },
        h('div', { class: 'grow ellipsis' }, h('strong', { class: 'ellipsis block' }, n.title), h('small', { class: 'muted ellipsis block' }, `${ago(n.updated_at)} · ${n.body.slice(0, 40)}`)))));
    }

    async function flush() {
      if (!saveTimer || !current) return;
      clearTimeout(saveTimer);
      saveTimer = null;
      await attempt(() => api(`notes/${current.id}`, { method: 'PUT', body: { title: current.title, body: current.body } }));
    }

    async function open(n) {
      await flush();
      current = n;
      drawList();
      const title = h('input', { class: 'note-title', value: n.title, maxlength: 100, placeholder: 'Titre' });
      const text = h('textarea', { class: 'note-body', maxlength: 10000, placeholder: 'Écrivez ici…' });
      text.value = n.body;
      const status = h('small', { class: 'muted' }, 'Enregistré');
      const changed = () => {
        n.title = title.value;
        n.body = text.value;
        n.updated_at = Date.now();
        status.textContent = 'Modification…';
        clearTimeout(saveTimer);
        saveTimer = setTimeout(async () => {
          saveTimer = null;
          if (await attempt(() => api(`notes/${n.id}`, { method: 'PUT', body: { title: n.title, body: n.body } }))) status.textContent = 'Enregistré';
          drawList();
        }, 800);
      };
      title.addEventListener('input', changed);
      text.addEventListener('input', changed);
      clear(editor,
        h('div', { class: 'row between' }, status, h('button', { class: 'icon-btn danger', title: 'Supprimer', onclick: async () => {
          if (!await confirmBox(`Supprimer la note « ${n.title} » ?`, 'Supprimer')) return;
          clearTimeout(saveTimer);
          saveTimer = null;
          if (await attempt(() => api(`notes/${n.id}`, { method: 'DELETE', body: {} }))) { current = null; load(); }
        } }, icon('trash', 16))),
        title, text);
    }

    async function create() {
      await flush();
      const r = await attempt(() => api('notes', { method: 'POST', body: { title: 'Nouvelle note', body: '' } }));
      if (r) load(r.id);
    }

    load();
    return () => { flush(); };
  },
};
