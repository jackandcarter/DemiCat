const queue = [];
let processing = false;

function processQueue() {
  if (processing || queue.length === 0) return;
  processing = true;
  const { fn, resolve, reject } = queue.shift();
  Promise.resolve()
    .then(fn)
    .then(resolve, reject)
    .finally(() => {
      setTimeout(() => {
        processing = false;
        processQueue();
      }, 1000);
    });
}

function enqueue(fn) {
  return new Promise((resolve, reject) => {
    queue.push({ fn, resolve, reject });
    processQueue();
  });
}

module.exports = enqueue;
