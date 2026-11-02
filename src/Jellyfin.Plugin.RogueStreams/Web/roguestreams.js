export default function (view) {
    function list(data, name) {
        if (!data) {
            return [];
        }
        var pascal = name.charAt(0).toUpperCase() + name.slice(1);
        return data[name] || data[pascal] || [];
    }

    function noteOf(data) {
        return (data && (data.note || data.Note)) || '';
    }

    function req(method, path, body) {
        var client = window.ApiClient;
        if (!client) {
            return Promise.reject(new Error('ApiClient is not available on this page.'));
        }
        var url = client.getUrl('RogueStreams' + path);
        if (method === 'GET') {
            return client.getJSON(url);
        }
        return client.ajax({
            type: method,
            url: url,
            data: JSON.stringify(body || {}),
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    function bytes(n) {
        if (n == null) {
            return '';
        }
        if (n < 1048576) {
            return Math.round(n / 1024) + ' KB';
        }
        if (n < 1073741824) {
            return (n / 1048576).toFixed(1) + ' MB';
        }
        return (n / 1073741824).toFixed(2) + ' GB';
    }

    function who(stream) {
        var bits = [];
        list(stream, 'viewers').forEach(function (v) {
            bits.push((v.user || v.User || '?') + ' @ ' + (v.device || v.Device || '?'));
        });
        list(stream, 'recordings').forEach(function (r) {
            bits.push('rec: ' + r);
        });
        if (!bits.length) {
            return '<span style="color:#ff9b8a;">nobody</span>';
        }
        return '<span style="color:#7bd88f;">' + bits.join('<br>') + '</span>';
    }

    function q(id) {
        return view.querySelector('#' + id);
    }

    function render(d) {
        var note = q('rsNote');
        var noteText = noteOf(d);
        if (noteText) {
            note.style.display = 'block';
            note.textContent = noteText;
        } else {
            note.style.display = 'none';
        }
        q('rsError').style.display = 'none';

        var streams = list(d, 'streams');
        var tb = q('rsStreams');
        tb.innerHTML = '';
        if (!streams.length) {
            tb.innerHTML = '<tr><td colspan="5" style="padding:.5em .4em; opacity:.6;">No open live streams in this Jellyfin process.</td></tr>';
        }
        streams.forEach(function (s) {
            var tr = document.createElement('tr');
            tr.style.borderBottom = '1px solid rgba(255,255,255,.07)';
            var name = s.name || s.Name || 'live stream';
            var liveStreamId = s.liveStreamId || s.LiveStreamId || '';
            var consumers = s.consumerCount != null ? s.consumerCount : s.ConsumerCount;
            var path = s.path || s.Path || '';
            var fileBytes = s.fileBytes != null ? s.fileBytes : s.FileBytes;
            var inUse = s.inUse || s.InUse;
            tr.innerHTML =
                '<td style="padding:.4em;">' +
                    '<div>' + name + '</div>' +
                    '<div style="opacity:.5; font-size:.85em; word-break:break-all;">' + liveStreamId + '</div>' +
                '</td>' +
                '<td style="padding:.4em; opacity:.8;">' + consumers + '</td>' +
                '<td style="padding:.4em;">' + who(s) + '</td>' +
                '<td style="padding:.4em; opacity:.7;">' +
                    path +
                    (fileBytes != null ? '<div>' + bytes(fileBytes) + '</div>' : '') +
                '</td>' +
                '<td style="padding:.4em; text-align:right;"></td>';

            var btn = document.createElement('button');
            btn.setAttribute('is', 'emby-button');
            btn.className = 'raised button-cancel block';
            btn.textContent = 'Close';
            btn.addEventListener('click', function () {
                var warn = inUse
                    ? 'This stream looks in use (viewer or recording). Closing it will stop them.\n\nClose anyway?'
                    : 'Close this live stream and release its tuner?';
                if (!confirm(warn)) {
                    return;
                }
                btn.disabled = true;
                req('POST', '/Close', { liveStreamId: liveStreamId }).then(render).catch(function (e) {
                    alert(e.message || e);
                    btn.disabled = false;
                    refresh();
                });
            });
            tr.lastElementChild.appendChild(btn);
            tb.appendChild(tr);
        });

        var tuners = list(d, 'tuners');
        var tn = q('rsTuners');
        tn.innerHTML = '';
        if (!tuners.length) {
            tn.innerHTML = '<tr><td colspan="4" style="padding:.5em .4em; opacity:.6;">No HDHomeRun leases (or tuner URL not set / unreachable).</td></tr>';
        }
        tuners.forEach(function (t) {
            var tr = document.createElement('tr');
            tr.innerHTML =
                '<td style="padding:.3em .4em; opacity:.7;">' + (t.resource || t.Resource || '') + '</td>' +
                '<td style="padding:.3em .4em;">' + (t.channel || t.Channel || '') + '</td>' +
                '<td style="padding:.3em .4em;">' + (t.name || t.Name || '\u2014') + '</td>' +
                '<td style="padding:.3em .4em; opacity:.7;">' + ((t.networkRate || t.NetworkRate || 0) / 1000000).toFixed(2) + ' MB/s</td>';
            tn.appendChild(tr);
        });

        var sessions = list(d, 'sessions');
        var sn = q('rsSessions');
        sn.innerHTML = '';
        if (!sessions.length) {
            sn.innerHTML = '<tr><td style="padding:.4em; opacity:.6;">Nobody is watching live TV.</td></tr>';
        }
        sessions.forEach(function (s) {
            var tr = document.createElement('tr');
            tr.innerHTML =
                '<td style="padding:.3em .4em;">' + (s.user || s.User || '') + '</td>' +
                '<td style="padding:.3em .4em; opacity:.7;">' + (s.device || s.Device || '') + '</td>' +
                '<td style="padding:.3em .4em;">' + (s.channel || s.Channel || s.itemName || s.ItemName || '') + '</td>' +
                '<td style="padding:.3em .4em; opacity:.7;">' + (s.client || s.Client || '') + ' ' + (s.playMethod || s.PlayMethod || '') + '</td>';
            sn.appendChild(tr);
        });

        var recordings = list(d, 'recordings');
        var rn = q('rsRecordings');
        rn.innerHTML = '';
        if (!recordings.length) {
            rn.innerHTML = '<tr><td style="padding:.4em; opacity:.6;">No in-progress recordings.</td></tr>';
        }
        recordings.forEach(function (r) {
            var tr = document.createElement('tr');
            tr.innerHTML =
                '<td style="padding:.3em .4em;">' + (r.name || r.Name || '') + '</td>' +
                '<td style="padding:.3em .4em; opacity:.7;">' + (r.channel || r.Channel || '') + '</td>' +
                '<td style="padding:.3em .4em; opacity:.7;">' + (r.status || r.Status || '') + '</td>';
            rn.appendChild(tr);
        });

        q('rsStamp').textContent =
            'Updated ' + new Date().toLocaleTimeString() + ' \u2014 refreshes every 5s while this page is open.';
    }

    function refresh() {
        req('GET', '/Status').then(render).catch(function (e) {
            var err = q('rsError');
            err.style.display = 'block';
            err.textContent = e.message || String(e);
        });
    }

    var timer;
    view.addEventListener('viewshow', function () {
        refresh();
        timer = setInterval(refresh, 5000);
    });
    view.addEventListener('viewhide', function () {
        if (timer) {
            clearInterval(timer);
            timer = null;
        }
    });
}
