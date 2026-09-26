document.querySelectorAll('[data-copy]').forEach(button => {
  button.addEventListener('click', async () => {
    const label = button.textContent;
    try {
      await navigator.clipboard.writeText(button.dataset.copy);
      button.textContent = 'Copied';
    } catch {
      button.textContent = 'Select text';
    }
    setTimeout(() => button.textContent = label, 1800);
  });
});

const codecNames = ['FIC Fast', 'FIC Default', 'ImageSharp PNG', 'SkiaSharp PNG', 'ImageSharp JPEG q90', 'SkiaSharp JPEG q90'];
const chartColors = ['fic-fast', 'fic-default', 'imagesharp', 'skia', 'imagesharp-jpeg', 'skia-jpeg'];
const formatBytes = bytes => bytes >= 1e9 ? `${(bytes / 1e9).toFixed(2)} GB` : `${(bytes / 1e6).toFixed(1)} MB`;

async function loadCorpusCharts() {
  const select = document.querySelector('#corpus-folder');
  if (!select) return;
  try {
    const response = await fetch('benchmark-data.json');
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const data = await response.json();
    const folders = [...new Set(data.rows.map(row => row.folder))];
    for (const folder of folders) {
      const option = document.createElement('option');
      option.value = option.textContent = folder;
      select.append(option);
    }

    const render = () => {
      const rows = data.rows.filter(row => select.value === 'all' || row.folder === select.value);
      const groups = codecNames.map(codec => rows.filter(row => row.codec === codec).reduce((sum, row) => ({
        codec,
        images: sum.images + row.images,
        pixels: sum.pixels + row.megapixels * 1e6,
        bytes: sum.bytes + row.encoded_bytes,
        encode: sum.encode + row.encode_seconds,
        decode: sum.decode + row.decode_seconds
      }), { codec, images: 0, pixels: 0, bytes: 0, encode: 0, decode: 0 }));
      const reference = groups[0];
      document.querySelector('#corpus-summary').textContent = `${reference.images.toLocaleString()} images · ${(reference.pixels / 1e6).toFixed(0)} megapixels`;
      for (const [metric, value, label] of [
        ['size', item => item.bytes, item => formatBytes(item.bytes)],
        ['encode', item => item.pixels / item.encode / 1e6, item => `${(item.pixels / item.encode / 1e6).toFixed(1)} MP/s`],
        ['decode', item => item.pixels / item.decode / 1e6, item => `${(item.pixels / item.decode / 1e6).toFixed(1)} MP/s`]
      ]) {
        const container = document.querySelector(`[data-chart="${metric}"]`);
        container.replaceChildren();
        const shown = metric === 'size' ? groups.slice(0, 4) : groups;
        const max = Math.max(...shown.map(value));
        for (const [index, item] of shown.entries()) {
          const row = document.createElement('div');
          row.className = 'chart-row';
          const name = document.createElement('span');
          name.className = 'chart-label';
          name.textContent = item.codec;
          const track = document.createElement('span');
          track.className = 'chart-track';
          const bar = document.createElement('span');
          bar.className = `chart-bar ${chartColors[index]}`;
          bar.style.width = `${Math.max(1, value(item) / max * 100)}%`;
          track.append(bar);
          const amount = document.createElement('span');
          amount.className = 'chart-value';
          amount.textContent = label(item);
          row.append(name, track, amount);
          container.append(row);
        }
      }
    };
    select.addEventListener('change', render);
    render();
  } catch {
    document.querySelector('#corpus-summary').textContent = 'Benchmark charts unavailable; see the README for results.';
  }
}

loadCorpusCharts();
