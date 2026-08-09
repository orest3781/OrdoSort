(() => {
  const html = document.documentElement;
  html.classList.add('js');

  const REDUCE = matchMedia('(prefers-reduced-motion: reduce)').matches;

  // --- color scheme switcher ------------------------------------------
  // 7 keys + isDark only; the actual colors live in styles.css.
  const SCHEMES = [
    ['paper', false], ['graphite', true], ['ledger', true], ['microfilm', true],
    ['manila', false], ['carbon', true], ['blueprint', false]
  ];
  const chips = [...document.querySelectorAll('.chip')];

  function apply(key) {
    const scheme = SCHEMES.find(s => s[0] === key);
    if (!scheme) return;
    html.dataset.theme = key;
    html.classList.toggle('force-dark', scheme[1]);
    html.classList.toggle('force-light', !scheme[1]);
    chips.forEach(c => c.setAttribute('aria-pressed', String(c.dataset.scheme === key)));
  }

  let idx = -1;
  let timer;
  let cycling = !REDUCE && chips.length > 0;

  function stopCycling() {
    if (!cycling) return;
    cycling = false;
    clearInterval(timer);
  }

  chips.forEach(chip => {
    chip.addEventListener('click', () => {
      stopCycling();
      apply(chip.dataset.scheme);
    });
  });

  if (cycling) {
    timer = setInterval(() => {
      idx = (idx + 1) % SCHEMES.length;
      apply(SCHEMES[idx][0]);
    }, 4000);
    const stop = () => stopCycling();
    addEventListener('pointerdown', stop, { once: true, passive: true });
    addEventListener('keydown', stop, { once: true });
  }

  // --- scroll reveals ---------------------------------------------------
  if (!REDUCE && 'IntersectionObserver' in window) {
    const targets = document.querySelectorAll('.steps .card, .feature-grid .card');
    const io = new IntersectionObserver(entries => {
      entries.forEach(entry => {
        if (entry.isIntersecting) {
          entry.target.classList.add('in-view');
          io.unobserve(entry.target);
        }
      });
    }, { threshold: 0.2 });
    targets.forEach(t => io.observe(t));
  }
})();
