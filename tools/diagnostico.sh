#!/bin/sh
# Resumen de las últimas reproducciones de TV en directo en Emby (y del plugin en Dispatcharr), sin claves.
# Uso: sudo sh diagnostico.sh [nº de sesiones, 6 por defecto]   -> deja el resultado en /tmp/diagnostico.txt
LOGDIR="${LOGDIR:-/var/lib/emby/logs}"
N="${1:-6}"
OUT=/tmp/diagnostico.txt
LOGS=$(ls -tr "$LOGDIR"/embyserver*.txt 2>/dev/null | tail -3)
[ -n "$LOGS" ] || { echo "No hay logs en $LOGDIR"; exit 1; }
{
cat $LOGS | perl -e '
  my $n = shift @ARGV; my (%who, %ev, %cnt, %err, @order, %first);
  while (<STDIN>) {
    s/\xe2\x80[\x8b\x8c\x8d]//g;
    s/(api_key|X-Emby-Token|Token)=[^&" ]*/$1=XXX/g;
    my ($t) = /^\S+ (\S+)/;
    my ($id) = /PlaySessionId[=:] ?(\w+)/;
    if (/reported by app (.+?) on (.+?) playing (.+?)\. Position/ && $id) { $who{$id} = "$2 | $1 | $3" }
    next unless $id;
    push @order, $id unless $first{$id}++;
    if (/Response (\d+) to .*? Time: (\d+)ms\. GET \S*\/([\w.]+)\?/) {
      my ($code, $ms, $name) = ($1, $2, $3);
      my ($sub) = /CurrentSubtitleStreamIndex=(\d+)/;
      if ($name =~ /\.m3u8$/) {
        $name .= " (sub $sub)" if $name =~ /subtitles/ && defined $sub;
        my $last = $ev{$id}[-1];
        if ($last && $last->[1] eq "$name $code" && @{$ev{$id}} > 6) { $last->[3]++; $last->[2] = $ms if $ms > $last->[2] }
        else { push @{$ev{$id}}, [$t, "$name $code", $ms, 1] }
      } else {
        my $kind = $name =~ /\.vtt$/ ? "vtt" : "seg";
        $cnt{$id}{"$kind $code"}++;
        $cnt{$id}{"$kind lentos (>2 s)"}++ if $ms > 2000;
      }
    }
    if (/ Error /) { my $m = substr($_, 24, 220); $m =~ s/\s+$//; $err{$id}{$m}++ }
  }
  my @ids = @order > $n ? @order[-$n .. -1] : @order;
  for my $id (@ids) {
    print "\n=== $id  ", ($who{$id} // "(sin datos del dispositivo)"), "\n";
    my @e = @{$ev{$id} || []};
    my $max = 40;
    for (@e[0 .. ($#e < $max ? $#e : $max)]) { printf "  %s  %-34s %6d ms%s\n", $_->[0], $_->[1], $_->[2], $_->[3] > 1 ? "  (x$_->[3])" : "" }
    print "  ... (", scalar(@e) - $max - 1, " más)\n" if @e > $max + 1;
    print "  segmentos: ", join(", ", map { "$_: $cnt{$id}{$_}" } sort keys %{$cnt{$id} || {}}), "\n";
    my @k = sort { $err{$id}{$b} <=> $err{$id}{$a} } keys %{$err{$id} || {}};
    print "  ERROR x$err{$id}{$_}: $_\n" for @k[0 .. ($#k < 5 ? $#k : 5)];
  }' "$N"
echo
echo "=== Errores recientes de Emby (sin sesión)"
tail -n 20000 $(echo "$LOGS" | tail -1) | grep -E ' Error ' | grep -v PlaySessionId | sed -E 's/(api_key|X-Emby-Token|Token)=[^&" ]*/\1=XXX/g; s/^.{24}//' | cut -c1-200 | sort | uniq -c | sort -rn | head -12
echo
echo "=== Últimas conversiones (ffmpeg)"
for F in $(ls -t "$LOGDIR"/ffmpeg-transcode-*.txt 2>/dev/null | head -4); do
  echo "-- $(basename "$F")  $(ls -l --time-style='+%F %T' "$F" | awk '{print $6, $7}')"
  grep -m6 -E 'Stream #0:[0-9]+.*(Subtitle|Video|Audio)' "$F" | cut -c1-150 | sed 's/^/   /'
  grep -iE 'error|invalid|failed|410|404|non-monoton|discontinuity' "$F" | sed -E 's/(api_key|X-Emby-Token|Token)=[^&" ]*/\1=XXX/g' | cut -c1-180 | sort | uniq -c | sort -rn | head -6 | sed 's/^/   /'
  grep -oE 'speed=[ 0-9.x]+' "$F" | tail -1 | sed 's/^/   último /'
done
if command -v docker >/dev/null 2>&1; then
  echo
  echo "=== Dispatcharr (plugin, últimas 24 h)"
  docker logs --since 24h dispatcharr 2>&1 | grep -iE 'intro_relay|irelay|ghost|No clients remain|410|subt' | sed -E 's/([?&](u|p|api_key|token)=)[^&" ]*/\1XXX/g; s#/u=[^/]*/p=[^/ "]*#/u=XXX/p=XXX#g' | cut -c1-220 | tail -60
fi
} > "$OUT" 2>&1
echo "Hecho: $OUT ($(wc -l < "$OUT") líneas). Pégamelo o adjúntalo."
