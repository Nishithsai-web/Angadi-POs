const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
(async () => {
  const [,, input, out, w, h] = process.argv;
  const b = await chromium.launch();
  const p = await b.newPage({ viewport: { width: +w, height: +h } });
  await p.goto('file://' + require('path').resolve(input));
  await p.screenshot({ path: out, omitBackground: true });
  await b.close();
})();
