(() => {
  const html = document.documentElement;
  html.classList.add('js');

  const REDUCE = matchMedia('(prefers-reduced-motion: reduce)').matches;

  // --- theme toggle ------------------------------------------------------
  // Auto follows the system (no data-theme, the CSS media query decides);
  // Light and Dark pin it and are remembered. The saved choice is applied
  // before first paint by the inline script in <head>; this wires the
  // buttons and the gallery's forced screenshot variants.
  const KEY = 'ordosort-theme';
  const toggle = document.querySelector('.theme-toggle');
  const buttons = toggle ? [...toggle.querySelectorAll('button')] : [];

  function apply(choice) {
    if (choice === 'light' || choice === 'dark') html.dataset.theme = choice;
    else delete html.dataset.theme;
    html.classList.toggle('force-dark', choice === 'dark');
    html.classList.toggle('force-light', choice === 'light');
    buttons.forEach(b => b.setAttribute('aria-pressed', String(b.dataset.themeChoice === choice)));
  }

  if (toggle) {
    toggle.hidden = false;
    apply(html.dataset.theme || 'auto');
    buttons.forEach(b => b.addEventListener('click', () => {
      const choice = b.dataset.themeChoice;
      apply(choice);
      try {
        if (choice === 'auto') localStorage.removeItem(KEY);
        else localStorage.setItem(KEY, choice);
      } catch (e) { /* storage blocked: the choice lasts for this visit only */ }
    }));
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
