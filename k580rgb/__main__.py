"""python -m k580rgb [--port N] [--no-browser] [--console] [--no-resume]

Runs in the system tray by default; --console runs in the terminal instead (Ctrl+C quits).
"""
import argparse
import json
import logging
import logging.handlers
import os
import sys
import threading
import urllib.request
import webbrowser

from . import __version__
from .automation import Automation
from .controller import CONFIG_DIR, LOG_PATH, Controller
from .server import serve

log = logging.getLogger('k580rgb')


def setup_logging():
    os.makedirs(CONFIG_DIR, exist_ok=True)
    fmt = logging.Formatter('%(asctime)s %(levelname)s %(message)s')
    handlers = [logging.handlers.RotatingFileHandler(LOG_PATH, maxBytes=1_000_000, backupCount=2)]
    if sys.stderr:                     # pythonw has no console
        handlers.append(logging.StreamHandler())
    log.setLevel(logging.INFO)
    for h in handlers:
        h.setFormatter(fmt)
        log.addHandler(h)


def already_running(url):
    """True if K580 RGB already answers on this port."""
    try:
        with urllib.request.urlopen(url + 'api/static', timeout=2) as r:
            return 'version' in json.load(r)
    except (OSError, ValueError):
        return False


def main():
    ap = argparse.ArgumentParser(prog='k580rgb', description='Redragon K580 lighting controller')
    ap.add_argument('--port', type=int, default=5800)
    ap.add_argument('--no-browser', action='store_true', help="don't open the UI on start")
    ap.add_argument('--console', action='store_true', help='run in the terminal instead of the tray')
    ap.add_argument('--no-resume', action='store_true', help="don't resume the last live effect")
    args = ap.parse_args()
    setup_logging()
    url = 'http://127.0.0.1:%d/' % args.port

    controller = Controller()
    automation = Automation(controller)
    try:
        httpd = serve(controller, automation, args.port)
    except OSError:
        # port taken: if it's us, just bring up the running app instead of a second copy
        if already_running(url):
            log.info('K580 RGB is already running; opening it')
            if not args.no_browser:
                webbrowser.open(url)
            return
        raise

    controller.start()
    automation.start()
    if not args.no_resume:
        controller.restore_last_live()
    log.info('K580 RGB %s running at %s', __version__, url)
    if not args.no_browser:
        webbrowser.open(url)

    try:
        if args.console:
            print('K580 RGB %s running at %s  (Ctrl+C to quit)' % (__version__, url))
            try:
                httpd.serve_forever()
            except KeyboardInterrupt:
                pass
        else:
            from .tray import Tray
            threading.Thread(target=httpd.serve_forever, daemon=True).start()
            Tray(controller, url).run()          # blocks until Quit
            httpd.shutdown()
    finally:
        controller.stop()
        log.info('K580 RGB stopped')


if __name__ == '__main__':
    main()
