"""Local web UI + JSON API (127.0.0.1 only).

POSTs must carry the header X-K580: 1. Browsers can't send custom headers
cross-origin without a CORS preflight (which we never approve), so other websites
can't drive the keyboard or wear its flash.
"""
import json
import os
import socket
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote

from . import __version__, effects, layout, modes, presets, sources
from .device import SIDE_COLORS, SIDE_MODES, Params
from .modes import CUSTOM

UI_DIR = os.path.join(os.path.dirname(__file__), 'ui')
PREVIEW_FPS = 20


def make_handler(controller, automation, port):
    allowed_hosts = {'127.0.0.1:%d' % port, 'localhost:%d' % port}

    static = {
        'layout': [k.to_dict() for k in layout.KEYS + layout.SIDE_KEYS],
        'iso_keys': [k.to_dict() for k in layout.ISO_KEYS],
        'size': [layout.WIDTH, layout.HEIGHT],
        'modes': modes.as_list(),
        'custom_mode': CUSTOM,
        'brightness_max': modes.BRIGHTNESS_MAX,
        'speed_max': modes.SPEED_MAX,
        'effects': effects.as_list(),
        'zones': effects.ZONES,
        'side_modes': SIDE_MODES,
        'side_colors': SIDE_COLORS,
        'bounds': layout.BOUNDS,
        'blends': effects.BLENDS,
        'presets': presets.BUILTIN,
        'version': __version__,
        'sources': sources.available(),
    }

    def apply(body):
        params = Params.from_dict(body['params']) if body.get('params') else None
        custom = [tuple(c) for c in body['custom']] if body.get('custom') else None
        side = [int(v) for v in body['side']] if body.get('side') else None
        if side and not (0 <= side[0] < len(SIDE_MODES) and 0 <= side[1] <= 5 and 0 <= side[2] < len(SIDE_COLORS)):
            raise ValueError('side settings out of range')
        controller.request(params, custom, side, now=bool(body.get('now')))

    def save_profile(body):
        name = body['name'].strip()
        if not name:
            raise ValueError('empty name')
        params, custom, side = controller.desired()
        controller.config['profiles'][name] = {
            'params': params.to_dict(),
            'custom': [list(c) for c in custom] if params.mode == CUSTOM else None,
            'side': list(side) if side else None,
        }
        controller.save_config()

    def save_preset(body):
        name = str(body['name']).strip()[:40]
        layers = body['layers']
        if not name:
            raise ValueError('empty name')
        if not isinstance(layers, list) or not 1 <= len(layers) <= 8:
            raise ValueError('a preset needs 1-8 layers')
        for layer in layers:
            if not isinstance(layer, dict) or layer.get('effect') not in effects.EFFECTS:
                raise ValueError('unknown effect in preset')
        presets = controller.config.setdefault('live_presets', {})
        if name not in presets and len(presets) >= 100:
            raise ValueError('too many presets (100 max)')
        presets[name] = layers
        controller.save_config()

    def delete_preset(body):
        controller.config.setdefault('live_presets', {}).pop(body['name'], None)
        controller.save_config()

    def delete_profile(body):
        controller.config['profiles'].pop(body['name'], None)
        controller.config['rules'] = [r for r in controller.config['rules'] if r['profile'] != body['name']]
        if controller.config['default_profile'] == body['name']:
            controller.config['default_profile'] = None
        controller.save_config()

    def set_automation(body):
        cfg = controller.config
        cfg['rules'] = [{'process': r['process'].strip().lower(), 'profile': r['profile']}
                        for r in body['rules'] if r.get('process', '').strip()]
        cfg['default_profile'] = body.get('default_profile') or None
        cfg['automation'] = bool(body.get('automation'))
        automation.applied = None
        controller.save_config()

    def live(body):
        controller.set_live(body.get('layers'), bool(body.get('stream')))

    def bake(body):
        controller.bake(body['layers'])

    def delete_media(body):
        from . import media
        media.delete(body['name'])

    def rocket_mania_status():
        from .games import ROCKET_MANIA as rm
        now = time.monotonic()
        last = {}
        if rm.spotter:
            for t, kind in rm.spotter.events:
                last[kind] = round(now - t, 1)
        return {'folder': rm.folder, 'error': rm.error, 'last': last}

    def media_list():
        if not sources.available()['media']:
            return []
        from . import media
        return media.library()

    posts = {
        '/api/media/delete': delete_media,
        '/api/apply': apply,
        '/api/profiles/save': save_profile,
        '/api/profiles/delete': delete_profile,
        '/api/presets/save': save_preset,
        '/api/presets/delete': delete_preset,
        '/api/profiles/apply': lambda b: controller.apply_profile(b['name']),
        '/api/automation': set_automation,
        '/api/live': live,
        '/api/bake': bake,
    }

    class Handler(BaseHTTPRequestHandler):
        protocol_version = 'HTTP/1.1'

        def log_message(self, *a):
            pass

        def _send(self, code, body, ctype='application/json'):
            data = body if isinstance(body, bytes) else json.dumps(body).encode()
            self.send_response(code)
            self.send_header('Content-Type', ctype)
            self.send_header('Content-Length', str(len(data)))
            self.send_header('Cache-Control', 'no-store')
            self.end_headers()
            self.wfile.write(data)

        def _host_ok(self):
            if self.headers.get('Host') in allowed_hosts:
                return True
            self._send(403, {'error': 'forbidden'})
            return False

        def do_GET(self):
            if not self._host_ok():
                return
            if self.path in ('/', '/index.html'):
                with open(os.path.join(UI_DIR, 'index.html'), 'rb') as f:
                    self._send(200, f.read(), 'text/html; charset=utf-8')
            elif self.path == '/api/static':
                self._send(200, static)
            elif self.path == '/api/state':
                self._send(200, {**controller.snapshot(), 'recent_apps': automation.recent_apps,
                                 'media': media_list(),
                                 'live_presets': controller.config.get('live_presets', {}),
                                 'rocket_mania': rocket_mania_status(),
                                 'source_errors': {'audio': sources.AUDIO.error, 'screen': sources.SCREEN.error}})
            elif self.path == '/api/frames':
                self._stream_frames()
            else:
                self._send(404, {'error': 'not found'})

        def do_POST(self):
            if not self._host_ok():
                return
            if self.headers.get('X-K580') != '1':
                return self._send(403, {'error': 'missing X-K580 header'})
            n = int(self.headers.get('Content-Length', 0))
            if self.path == '/api/media/upload':
                return self._upload(n)
            handler = posts.get(self.path)
            if not handler:
                return self._send(404, {'error': 'not found'})
            try:
                handler(json.loads(self.rfile.read(n) or b'{}'))
            except (KeyError, ValueError, TypeError, OSError) as e:
                return self._send(400, {'error': '%s: %s' % (type(e).__name__, e)})
            self._send(200, {'ok': True})

        def _upload(self, n):
            from . import media
            if n > media.MAX_UPLOAD:
                self.close_connection = True
                return self._send(413, {'error': 'file larger than 25 MB'})
            try:
                name = media.save_upload(unquote(self.headers.get('X-Filename', 'image.gif')), self.rfile.read(n))
            except ValueError as e:
                return self._send(400, {'error': str(e)})
            self._send(200, {'ok': True, 'name': name})

        def _stream_frames(self):
            self.send_response(200)
            self.send_header('Content-Type', 'text/event-stream')
            self.send_header('Cache-Control', 'no-store')
            self.end_headers()
            try:
                while True:
                    if controller.live:
                        data = bytes(v for c in controller.frame for v in c).hex()
                        self.wfile.write(b'data: ' + data.encode() + b'\n\n')
                        self.wfile.flush()
                    time.sleep(1 / PREVIEW_FPS)
            except (ConnectionError, OSError):
                pass

    return Handler


class Server(ThreadingHTTPServer):
    # On Windows SO_REUSEADDR (the http.server default) lets a second process bind the same
    # port, so two app copies would run at once. Bind exclusively so the second one fails.
    allow_reuse_address = False

    def server_bind(self):
        if hasattr(socket, 'SO_EXCLUSIVEADDRUSE'):
            self.socket.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
        super().server_bind()

    def handle_error(self, request, client_address):
        if not isinstance(sys.exc_info()[1], ConnectionError):   # browser closed a tab: not an error
            super().handle_error(request, client_address)


def serve(controller, automation, port):
    httpd = Server(('127.0.0.1', port), make_handler(controller, automation, port))
    httpd.daemon_threads = True
    return httpd
