/*
 * Keeps the version number, download link and checksum on this site in step with the repository's latest
 * official release, so nobody has to edit the pages when a release is published.
 *
 * Configure it with data attributes on the <script> tag:
 *   data-repo   "owner/name"
 *   data-asset  a regular expression that picks the mod ZIP out of a release's files (default: any .zip)
 *
 * Mark up the page with:
 *   data-release="version"       the text becomes the version without a leading "v" (1.2.3)
 *   data-release="tag"           the text becomes the tag (v1.2.3)
 *   data-release="asset-name"    the text becomes the ZIP's file name
 *   data-release="sha256"       the text becomes the ZIP's SHA-256
 *   data-release-href="download" the link goes to the ZIP
 *   data-release-href="notes"    the link goes to the release page
 *   data-release-show="prerelease" or "stable"   the element is hidden unless the release is of that kind
 *   data-release-pinned="1.2.3"  this text was written for that version. When the newest release is another one,
 *                                a note saying so is added at the end of the element.
 *
 * The HTML always holds working values: links to /releases/latest and the version last written by hand. With no
 * script, no network or a rate-limited lookup the page therefore still works, and the buttons still lead to the
 * newest release.
 *
 * "Latest" is what GitHub marks as Latest: the newest published release that is not a draft or a pre-release.
 * A repository with only pre-releases falls back to its newest pre-release. The answer is kept in
 * localStorage for 30 minutes, because unauthenticated visitors get 60 GitHub lookups an hour per IP address.
 * Only text and href values are ever written, never markup.
 */
(function () {
  'use strict';

  var script = document.currentScript;
  if (!script || !window.fetch) return;

  var repo = script.getAttribute('data-repo') || '';
  if (!/^[\w.-]+\/[\w.-]+$/.test(repo)) return;
  var pattern;
  try { pattern = new RegExp(script.getAttribute('data-asset') || '\\.zip$', 'i'); } catch (e) { pattern = /\.zip$/i; }

  var API = 'https://api.github.com/repos/' + repo + '/releases';
  var PREFIX = 'https://github.com/' + repo + '/';
  var CACHE_KEY = 'tbmods.release.v1.' + repo;
  var CACHE_MS = 30 * 60 * 1000;

  function readCache() {
    try {
      var c = JSON.parse(localStorage.getItem(CACHE_KEY));
      return c && c.rel && Date.now() - c.at < CACHE_MS ? c.rel : null;
    } catch (e) { return null; }
  }
  function writeCache(rel) {
    try { localStorage.setItem(CACHE_KEY, JSON.stringify({ at: Date.now(), rel: rel })); } catch (e) { /* storage blocked: fine */ }
  }

  // Reduce a GitHub release to the few values the page uses, or null when it is unusable or looks wrong.
  function shape(r) {
    if (!r || r.draft) return null;
    var tag = String(r.tag_name || '');
    if (!/^[\w.+-]{1,40}$/.test(tag)) return null;
    var asset = null;
    var files = r.assets || [];
    for (var i = 0; i < files.length; i++) {
      if (files[i] && /\.zip$/i.test(files[i].name || '') && pattern.test(files[i].name)) { asset = files[i]; break; }
    }
    if (!asset || !/^[\w.+-]{1,120}$/.test(String(asset.name))) return null;
    var url = String(asset.browser_download_url || '');
    var notes = String(r.html_url || '');
    if (url.indexOf(PREFIX) !== 0 || notes.indexOf(PREFIX) !== 0) return null;
    var digest = /^sha256:([0-9a-f]{64})$/i.exec(String(asset.digest || ''));
    return {
      tag: tag,
      version: tag.replace(/^v(?=\d)/i, ''),
      prerelease: !!r.prerelease,
      notes: notes,
      asset: { name: String(asset.name), url: url, sha256: digest ? digest[1].toLowerCase() : '' }
    };
  }

  function getJson(url) {
    return fetch(url, { headers: { Accept: 'application/vnd.github+json' } }).then(function (res) {
      return res.ok ? res.json().then(function (json) { return { ok: true, json: json }; }) : { ok: false, status: res.status };
    });
  }

  function load() {
    return getJson(API + '/latest').then(function (r) {
      if (r.ok) return shape(r.json);
      if (r.status !== 404) return null; // rate limited or an outage: leave the page as written
      // No stable release yet: use the newest published pre-release.
      return getJson(API + '?per_page=10').then(function (l) {
        if (!l.ok || !Array.isArray(l.json)) return null;
        for (var i = 0; i < l.json.length; i++) {
          var s = shape(l.json[i]);
          if (s) return s;
        }
        return null;
      });
    });
  }

  function each(selector, fn) {
    var nodes = document.querySelectorAll(selector);
    for (var i = 0; i < nodes.length; i++) fn(nodes[i]);
  }

  // Text that was written for one specific version: say so when a newer release exists.
  function pin(el, rel) {
    var made = el.getAttribute('data-release-pinned').replace(/^v(?=\d)/i, '');
    var note = null;
    for (var i = 0; i < el.children.length; i++) {
      if (el.children[i].className === 'pinned-note') note = el.children[i];
    }
    if (made === rel.version) { if (note) el.removeChild(note); return; }
    if (note) return;
    var p = document.createElement('p');
    p.className = 'pinned-note';
    p.appendChild(document.createTextNode('This was written for ' + made + '. The newest release is '));
    var a = document.createElement('a');
    a.setAttribute('href', rel.notes);
    a.textContent = rel.tag;
    p.appendChild(a);
    p.appendChild(document.createTextNode(', so check its release notes for what has changed.'));
    el.appendChild(p);
  }

  function apply(rel) {
    var text = { version: rel.version, tag: rel.tag, 'asset-name': rel.asset.name, sha256: rel.asset.sha256 };
    each('[data-release]', function (el) {
      var v = text[el.getAttribute('data-release')];
      if (v) el.textContent = v;
    });
    var links = { download: rel.asset.url, notes: rel.notes };
    each('[data-release-href]', function (el) {
      var v = links[el.getAttribute('data-release-href')];
      if (v) el.setAttribute('href', v);
    });
    each('[data-release-show]', function (el) {
      var kind = el.getAttribute('data-release-show');
      if (kind === 'prerelease') el.hidden = !rel.prerelease;
      else if (kind === 'stable') el.hidden = rel.prerelease;
    });
    each('[data-release-pinned]', function (el) { pin(el, rel); });
    document.documentElement.setAttribute('data-release-ready', rel.tag);
    document.dispatchEvent(new CustomEvent('tbmods:release', { detail: rel }));
  }

  function run() {
    var cached = readCache();
    if (cached) { apply(cached); return; }
    load().then(function (rel) {
      if (!rel) return;
      writeCache(rel);
      apply(rel);
    }).catch(function () { /* network error: keep the page as written */ });
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', run);
  else run();
})();
