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

const converter = document.querySelector('#convert-form');
if (converter) {
  const input = document.querySelector('#convert-file');
  const format = document.querySelector('#output-format');
  const status = document.querySelector('#convert-status');
  const result = document.querySelector('#convert-result');
  const preview = document.querySelector('#convert-preview');
  const download = document.querySelector('#convert-download');
  const button = document.querySelector('#convert-button');
  const maxPixels = 24_000_000;
  let previewUrl, downloadUrl;
  const number = new Intl.NumberFormat('en-US');
  const size = bytes => bytes < 1000 ? `${bytes} B` : bytes < 1e6 ? `${(bytes / 1000).toFixed(2)} kB` : `${(bytes / 1e6).toFixed(2)} MB`;
  const setStat = (id, value) => { document.getElementById(id).textContent = value; };
  const isTfic = async file => {
    const head = new Uint8Array(await file.slice(0, 5).arrayBuffer());
    return head.length === 5 && head[0] === 84 && head[1] === 70 && head[2] === 73 && head[3] === 67 && head[4] === 0;
  };
  const canvasBlob = (canvas, mime, quality) => new Promise((resolve, reject) => {
    canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error('The browser could not encode this image.')), mime, quality);
  });
  const imageCanvas = async file => {
    const bitmap = await createImageBitmap(file);
    try {
      if (bitmap.width * bitmap.height > maxPixels) throw new Error('This browser converter is limited to 24 million pixels.');
      const canvas = document.createElement('canvas');
      canvas.width = bitmap.width; canvas.height = bitmap.height;
      const context = canvas.getContext('2d', { willReadFrequently: true });
      if (!context) throw new Error('Canvas is unavailable.');
      context.drawImage(bitmap, 0, 0);
      return { canvas, rgba: context.getImageData(0, 0, canvas.width, canvas.height).data };
    } finally { bitmap.close(); }
  };
  const pixelsCanvas = ({ pixels, width, height, channels }) => {
    if (width * height > maxPixels) throw new Error('This browser converter is limited to 24 million pixels.');
    const canvas = document.createElement('canvas');
    canvas.width = width; canvas.height = height;
    const context = canvas.getContext('2d');
    if (!context) throw new Error('Canvas is unavailable.');
    const image = context.createImageData(width, height);
    if (channels === 4) image.data.set(pixels);
    else for (let p = 0, q = 0; p < pixels.length; p += 3, q += 4) {
      image.data[q] = pixels[p]; image.data[q + 1] = pixels[p + 1];
      image.data[q + 2] = pixels[p + 2]; image.data[q + 3] = 255;
    }
    context.putImageData(image, 0, 0);
    return canvas;
  };
  const nextFrame = () => new Promise(resolve => setTimeout(resolve, 0));

  input.addEventListener('change', async () => {
    result.hidden = true;
    const file = input.files?.[0];
    if (!file) { status.textContent = 'Choose a file to begin.'; format.disabled = true; return; }
    const tfic = await isTfic(file);
    format.disabled = !tfic;
    status.textContent = tfic ? 'TFIC detected. Choose an output format, then convert.' : 'Image detected. It will be encoded as TFIC.';
  });

  converter.addEventListener('submit', async event => {
    event.preventDefault();
    const file = input.files?.[0];
    if (!file) return;
    button.disabled = true; result.hidden = true;
    status.textContent = 'Converting…';
    await nextFrame();
    const started = performance.now();
    try {
      const { encode, decode, getInfo } = await import('./tfic.js');
      const tfic = await isTfic(file);
      const stem = file.name.replace(/\.[^.]*$/, '') || 'image';
      let output, outputMime, canvas, width, height, channels, tficBytes;
      if (tfic) {
        const source = new Uint8Array(await file.arrayBuffer());
        const info = getInfo(source);
        if (info.width * info.height > maxPixels) throw new Error('This browser converter is limited to 24 million pixels.');
        const decoded = decode(source);
        ({ width, height, channels } = decoded);
        canvas = pixelsCanvas(decoded);
        outputMime = format.value;
        if (outputMime === 'image/jpeg') {
          const flat = document.createElement('canvas');
          flat.width = width; flat.height = height;
          const context = flat.getContext('2d');
          context.fillStyle = '#fff'; context.fillRect(0, 0, width, height);
          context.drawImage(canvas, 0, 0);
          canvas = flat;
        }
        output = await canvasBlob(canvas, outputMime, outputMime === 'image/webp' ? 1 : 0.92);
        if (output.type !== outputMime) throw new Error(`${outputMime} encoding is unavailable in this browser.`);
        tficBytes = file.size;
      } else {
        if (!['image/jpeg', 'image/png', 'image/webp'].includes(file.type)) throw new Error('Choose a JPEG, PNG, WebP, or TFIC file.');
        const image = await imageCanvas(file);
        width = image.canvas.width; height = image.canvas.height;
        const opaque = image.rgba.every((value, index) => (index & 3) !== 3 || value === 255);
        channels = opaque ? 3 : 4;
        let pixels = image.rgba;
        if (opaque) {
          pixels = new Uint8Array(width * height * 3);
          for (let p = 0, q = 0; p < image.rgba.length; p += 4) {
            pixels[q++] = image.rgba[p]; pixels[q++] = image.rgba[p + 1]; pixels[q++] = image.rgba[p + 2];
          }
        }
        const fileBytes = encode(pixels, width, height, channels);
        const decoded = decode(fileBytes); // Confirm checksum and make the preview from the output file.
        canvas = pixelsCanvas(decoded);
        output = new Blob([fileBytes], { type: 'application/octet-stream' });
        outputMime = 'application/octet-stream';
        tficBytes = fileBytes.length;
      }
      const ext = tfic ? ({ 'image/png': 'png', 'image/webp': 'webp', 'image/jpeg': 'jpg' })[outputMime] : 'tfic';
      const previewBlob = tfic ? output : await canvasBlob(canvas, 'image/png');
      if (previewUrl) URL.revokeObjectURL(previewUrl);
      if (downloadUrl) URL.revokeObjectURL(downloadUrl);
      previewUrl = URL.createObjectURL(previewBlob);
      downloadUrl = URL.createObjectURL(output);
      preview.src = previewUrl;
      download.href = downloadUrl;
      download.download = `${stem}.${ext}`;
      document.querySelector('#preview-caption').textContent = `${width} × ${height} preview of converted pixels`;
      document.querySelector('#result-title').textContent = `${tfic ? 'TFIC' : file.type.split('/')[1].toUpperCase()} → ${ext.toUpperCase()}`;
      const ratio = output.size / file.size;
      setStat('stat-dimensions', `${number.format(width)} × ${number.format(height)}`);
      setStat('stat-pixels', `${number.format(width * height * channels)} B · ${channels} channels`);
      setStat('stat-source', size(file.size));
      setStat('stat-output', size(output.size));
      setStat('stat-ratio', `${ratio.toFixed(3)}× · ${(ratio * 100).toFixed(1)}% of source`);
      setStat('stat-change', `${ratio < 1 ? ((1 - ratio) * 100).toFixed(1) + '% smaller' : ((ratio - 1) * 100).toFixed(1) + '% larger'}`);
      setStat('stat-time', `${(performance.now() - started).toFixed(0)} ms`);
      setStat('stat-bpp', `${(8 * tficBytes / (width * height)).toFixed(2)} bpp`);
      status.textContent = 'Conversion complete.';
      result.hidden = false;
    } catch (error) {
      status.textContent = error instanceof Error ? error.message : 'Conversion failed.';
    } finally { button.disabled = false; }
  });
}
