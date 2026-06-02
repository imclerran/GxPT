/* GxPT landing page — shared release lookup (ES5, no dependencies).
   Loaded on every page. One unauthenticated call to the GitHub Releases API
   populates, where present on the page:
     - the Download button's target (#download-btn) -> newest .msi
     - the download note's version (#download-note)
     - the sidebar "Latest release" widget version (#latest-ver)
   Release assets are uploaded manually, so the very latest tag may not have its
   installer yet; we walk back to the most recent release that does. On any
   failure every element keeps its static fallback. */
(function () {
  var btn = document.getElementById('download-btn');
  var note = document.getElementById('download-note');
  var latest = document.getElementById('latest-ver');
  var baseNote = 'Windows XP or later · .NET Framework 3.5';

  function findMsi(release) {
    var assets = (release && release.assets) || [];
    for (var i = 0; i < assets.length; i++) {
      if (/\.msi$/i.test(assets[i].name) && assets[i].browser_download_url) {
        return assets[i];
      }
    }
    return null;
  }

  function apply(release, msi) {
    // Normalize the tag's leading "v" so we never produce "vv0.15.0".
    var tag = release.tag_name ? String(release.tag_name).replace(/^v/i, '') : '';
    if (btn) { btn.setAttribute('data-href', msi.browser_download_url); }
    if (note && tag) { note.innerHTML = baseNote + ' · GxPT v' + tag; }
    if (latest && tag) { latest.innerHTML = 'GxPT v' + tag; }
  }

  try {
    var xhr = new XMLHttpRequest();
    xhr.open('GET', 'https://api.github.com/repos/imclerran/GxPT/releases?per_page=20', true);
    xhr.onreadystatechange = function () {
      if (xhr.readyState !== 4) return;
      if (xhr.status < 200 || xhr.status >= 300) return; // keep fallbacks
      try {
        var releases = JSON.parse(xhr.responseText);
        if (!releases || !releases.length) return;
        for (var i = 0; i < releases.length; i++) {
          if (releases[i].draft) continue;
          var msi = findMsi(releases[i]);
          if (msi) { apply(releases[i], msi); return; }
        }
        // No release carries an .msi yet — leave buttons on the releases page.
      } catch (e) { /* keep fallbacks */ }
    };
    xhr.send();
  } catch (e) { /* keep fallbacks */ }
})();
