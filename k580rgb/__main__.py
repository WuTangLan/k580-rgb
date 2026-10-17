"""python -m k580rgb [--port N] [--no-browser]"""
import argparse
import logging
import logging.handlers
import os
import webbrowser

from .automation import Automation
from .controller import CONFIG_DIR, LOG_PATH, Controller
from .server import serve


def setup_logging():
    os.makedirs(CONFIG_DIR, exist_ok=True)
    fmt = logging.Formatter('%(asctime)s %(levelname)s %(message)s')
    file = logging.handlers.RotatingFileHandler(LOG_PATH, maxBytes=1_000_000, backupCount=2)
    console = logging.StreamHandler()
    for h in (file, console):
        h.setFormatter(fmt)
    log = logging.getLogger('k580rgb')
    log.setLevel(logging.INFO)
    log.addHandler(file)
    log.addHandler(console)


def main():
    ap = argparse.ArgumentParser(prog='k580rgb', description='Redragon K580 lighting controller')
    ap.add_argument('--port', type=int, default=5800)
    ap.add_argument('--no-browser', action='store_true')
    args = ap.parse_args()
    setup_logging()

    controller = Controller()
    controller.start()
    automation = Automation(controller)
    automation.start()

    httpd = serve(controller, automation, args.port)
    url = 'http://127.0.0.1:%d/' % args.port
    print('K580 RGB running at %s  (Ctrl+C to quit)' % url)
    if not args.no_browser:
        webbrowser.open(url)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        controller.stop()


if __name__ == '__main__':
    main()
