// Geçit (.NET 10 WinForms + WebView2, Windows): agentkit hedefli denetimleri.
// Derleme: `dotnet build Gecit.slnx`. Arayüz wwwroot/ altında düz HTML/CSS/JS (derleme yok, node --check).
// Çeviri denetimi: tools/i18n-check.mjs (Türkçe kaynak metinlerin İngilizce karşılığı eksik mi).
// Uygulamayı açmak pencere açar ve yönetici izni isteyebilir: denetime girmez, kullanıcı dener.
import fs from 'node:fs';

export default ({ presets: P }) => ({
  maxHeavy: 2,
  checks: {
    syntax: P.node.syntax,
    build: P.node.run('dotnet build Gecit.slnx -nologo -v q -clp:ErrorsOnly', 'dotnet build', { light: true, limit: 240 }),
    i18n: P.node.run(fs.existsSync('tools/i18n-check.mjs') ? 'node tools/i18n-check.mjs' : 'echo "tools/i18n-check.mjs yok, atlandı"',
      'çeviri kapsamı (tr → en)', { light: true, limit: 60 }),
  },
  rules: [
    P.node.rule,
    [/\.(cs|csproj|slnx)$/, ['build']],
    [/^src\/Gecit\/(wwwroot\/|.*\.cs$)|^tools\/i18n/, ['i18n']],
    [/^src\/Gecit\/wwwroot\//, ['build']],
  ],
  reviewRules: ['.claude/review-rules.md'],
});
