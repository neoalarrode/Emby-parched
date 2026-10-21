#!/bin/sh
# Diagnóstico de reproducciones de Live TV en Emby a partir de los logs (también los antiguos).
#   sudo sh emby-sesiones.sh                  -> lista de sesiones: dispositivo, app, canal, PlaySessionId, inicio y fin
#   sudo sh emby-sesiones.sh <PlaySessionId>  -> detalle de esa sesión: IP, motivos de conversión, tiempos de entrega, ffmpeg
# No muestra api_key ni tokens.
LOGDIR="${LOGDIR:-/var/lib/emby/logs}"
LOGS=$(ls -tr "$LOGDIR"/embyserver*.txt 2>/dev/null)
[ -n "$LOGS" ] || { echo "No hay logs en $LOGDIR"; exit 1; }

if [ -z "$1" ]; then
  cat $LOGS | perl -ne '
    s/\xe2\x80[\x8b\x8c\x8d]//g;
    next unless /^(\S+ \S+).*reported by app (.+?) on (.+?) playing (.+?)\. Position: .*PlaySessionId: (\w+)/;
    my $k = "$5\t$3\t$2\t$4";
    $first{$k} //= $1; $last{$k} = $1;
    END {
      printf "%-19s  %-8s  %-32s  %s\n", "INICIO", "FIN", "PLAYSESSIONID", "DISPOSITIVO | APP | REPRODUCIENDO";
      for (sort { $first{$a} cmp $first{$b} } keys %first) {
        my ($id, $dev, $app, $item) = split /\t/;
        printf "%-19s  %-8s  %-32s  %s | %s | %s\n", substr($first{$_}, 0, 19), substr($last{$_}, 11, 8), $id, $dev, $app, $item;
      }
    }'
  exit 0
fi

ID="$1"
echo "== Sesión $ID"
cat $LOGS | perl -ne '
  BEGIN { $id = shift @ARGV }
  s/\xe2\x80[\x8b\x8c\x8d]//g;
  next unless /PlaySessionId[=:] ?\Q$id\E/;
  my ($t) = /^(\S+ \S+)/;
  if (/Source Ip: ([^,]+), .*?User-Agent=([^,]*)/ || /Source Ip: ([^,]+), .*?UserAgent: (.*)$/) { $ip{$1}++; $ua{$2}++ }
  if (/ GET (\S*\/(master|live|main)\.m3u8\?\S*)/ && !/Response/) {
    my ($url, $name) = ($1, $2);
    $url =~ s/\.$//;
    my @p = $url =~ /[?&]((?:TranscodeReasons|VideoCodec|AudioCodec|VideoBitrate|MaxStreamingBitrate|SegmentContainer|[A-Za-z0-9]*-?[Dd]einterlace)=[^&\s]*)/g;
    $req{"$name.m3u8: " . join(" ", @p)}++;
  }
  if (/\/hls1?\/(\w+)\/\w+?_(\d+)\.\w+/) { $dir{$1}++ }
  if (/Response (\d+) to [^.]*(?:\.[^.]*){3}\. Time: (\d+)ms\. GET \S*\/(?:hls1?\/\w+\/\w+_(\d+)\.\w+|(live|master|main)\.m3u8)/) {
    if (defined $3) { push @seg, [$t, $3, $2, $1] } else { push @pl, $2 }
  }
  if (/reported by app (.+?) on (.+?) playing (.+?)\. Position/) { $who = "$2 | $1 | $3" }
  END {
    print "Dispositivo: $who\n" if $who;
    print "IP: ", join(", ", keys %ip), "\n";
    print "User-Agent: ", join(" / ", keys %ua), "\n";
    print "Carpeta(s) de transcodificación: ", join(", ", map { "$_ ($dir{$_} peticiones)" } keys %dir), "\n";
    print "Peticiones de playlist:\n"; print "  $_\n" for sort keys %req;
    if (@seg) {
      my @ms = sort { $a <=> $b } map { $_->[2] } @seg;
      my $n = @ms; my $sum = 0; $sum += $_ for @ms;
      my $slow = grep { $_ > 2000 } @ms;
      my $err = grep { $_->[3] !~ /^2/ } @seg;
      printf "Segmentos entregados: %d  tiempo medio %d ms  mediana %d ms  p95 %d ms  max %d ms  >2 s: %d  errores HTTP: %d\n",
        $n, $sum / $n, $ms[int($n / 2)], $ms[int($n * 0.95)], $ms[-1], $slow, $err;
      print "Últimos 15 segmentos (hora, nº, ms, HTTP):\n";
      printf "  %s  #%s  %s ms  %s\n", @$_ for @seg[($#seg > 14 ? $#seg - 14 : 0) .. $#seg];
    }
    if (@pl) {
      my @ms = sort { $a <=> $b } @pl;
      printf "Playlists entregadas: %d  mediana %d ms  max %d ms\n", scalar @ms, $ms[int(@ms / 2)], $ms[-1];
    }
    print "DIRS=", join(" ", keys %dir), "\n";
  }' "$ID" > /tmp/emby-sesion-$$.txt
grep -v '^DIRS=' /tmp/emby-sesion-$$.txt
DIRS=$(sed -n 's/^DIRS=//p' /tmp/emby-sesion-$$.txt); rm -f /tmp/emby-sesion-$$.txt

for D in $DIRS; do
  FF=$(ls "$LOGDIR"/ffmpeg-*.txt 2>/dev/null | grep -v "graph\.txt$" | xargs -r grep -l "transcoding-temp/$D/" 2>/dev/null)
  echo "== ffmpeg de la carpeta $D"
  [ -n "$FF" ] || { echo "  (no se encuentran logs de ffmpeg)"; continue; }
  echo "  Logs de ffmpeg, con la hora de su última escritura (varios para la misma sesión = ffmpeg se reinició):"
  ls -l --time-style='+%F %T' $FF | awk '{print "   ", $6, $7, $NF}'
  for F in $FF; do
    echo "  -- $(basename "$F")"
    grep -m1 -oE ' -c:v:0 [^ ]+| -filter_complex "[^"]*"| -b:v:0 [0-9]+| -g:v:0 [0-9]+| -r:v:0 [0-9]+| -c:a:0 [^ ]+' "$F" | tr '\n' ' '; echo
    grep -m2 -E 'Stream #0:[01]' "$F" | sed 's/^/    /'
    grep -oE 'speed=[ 0-9.x]+' "$F" | tail -1 | sed 's/^/    último /'
    grep -iE 'error|invalid|corrupt|non-monoton|discontinuity' "$F" | grep -v 'discardcorrupt' | tail -3 | cut -c1-200 | sed 's/^/    /'
  done
done
