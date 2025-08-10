function log(level, ...args) {
  const ts = new Date().toISOString();
  console.log(`[${ts}] [${level}]`, ...args);
}

module.exports = {
  info: (...args) => log('INFO', ...args),
  warn: (...args) => log('WARN', ...args),
  error: (...args) => log('ERROR', ...args),
  debug: (...args) => log('DEBUG', ...args)
};

