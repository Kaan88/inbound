#pragma once

#include <QColor>
#include <QJsonArray>
#include <QDateTime>
#include <QPainter>
#include <QPainterPath>
#include <QMouseEvent>
#include <QPalette>
#include <QSizePolicy>
#include <QPointF>
#include <QSize>
#include <QWidget>
#include <algorithm>
#include <cmath>
#include <deque>

// A compact, download-only speed history panel for Qt 6 Widgets.
// The chart grows upward from its bottom edge and retains one minute of samples.
class SpeedGraph final : public QWidget {
public:
    explicit SpeedGraph(QWidget* parent = nullptr) : QWidget(parent) {
        setObjectName(QStringLiteral("speedGraph"));
        setAttribute(Qt::WA_OpaquePaintEvent);
        setMinimumHeight(kHandleHeight);
        setSizePolicy(QSizePolicy::Expanding, QSizePolicy::Fixed);
        setCursor(Qt::PointingHandCursor);
        setToolTip(tr("Click to expand or collapse the transfer speed graph"));
    }

    void addSample(double bytesPerSecond) {
        if (!std::isfinite(bytesPerSecond) || bytesPerSecond < 0.0)
            bytesPerSecond = 0.0;
        const qint64 now = QDateTime::currentMSecsSinceEpoch();
        if (!samples_.empty() && now < samples_.back().first)
            samples_.clear();
        samples_.emplace_back(now, bytesPerSecond);
        const qint64 cutoff = now - kHistoryMs;
        while (samples_.size() > 1 && samples_[1].first < cutoff)
            samples_.pop_front();
        update();
    }

    void setHistory(const QJsonArray &rates, int intervalSeconds) {
        samples_.clear();
        const qint64 step = qint64(std::max(1, intervalSeconds)) * 1000;
        const int first = std::max(0, int(rates.size()) - 1 - int(kHistoryMs / step));
        for (int index = first; index < rates.size(); ++index)
            samples_.emplace_back((index - first) * step, std::max(0.0, rates[index].toDouble()));
        update();
    }

    void setExpanded(bool expanded) {
        if (expanded_ == expanded) return;
        expanded_ = expanded;
        setFixedHeight(expanded_ ? kExpandedHeight : kHandleHeight);
        updateGeometry();
        update();
    }

    bool isExpanded() const noexcept { return expanded_; }
    QSize sizeHint() const override { return {480, expanded_ ? kExpandedHeight : kHandleHeight}; }

protected:
    void mousePressEvent(QMouseEvent* event) override {
        if (event->button() == Qt::LeftButton) {
            setExpanded(!expanded_);
            event->accept();
            return;
        }
        QWidget::mousePressEvent(event);
    }

    void paintEvent(QPaintEvent*) override {
        QPainter p(this);
        p.setRenderHint(QPainter::Antialiasing);
        const QColor base = palette().color(QPalette::Window);
        const QColor text = palette().color(QPalette::WindowText);
        const QColor muted = text;
        const QColor accent = palette().color(QPalette::HighlightedText);
        p.fillRect(rect(), base);
        p.setPen(QColor(text.red(), text.green(), text.blue(), 42));
        p.drawLine(0, 0, width(), 0);

        const QRect handle(0, height() - kHandleHeight, width(), kHandleHeight);
        p.fillRect(handle, palette().color(QPalette::Window));
        p.setPen(QColor(text.red(), text.green(), text.blue(), 145));
        p.setFont(font());
        const QString title = tr("Transfer speed");
        const double current = samples_.empty() ? 0.0 : samples_.back().second;
        const QString rate = formatRate(current);
        p.drawText(QRect(14, handle.top(), 150, handle.height()), Qt::AlignVCenter | Qt::AlignLeft, title);
        p.setPen(text);
        p.drawText(QRect(155, handle.top(), 130, handle.height()), Qt::AlignVCenter | Qt::AlignLeft, rate);
        const int cx = width() - 22;
        const int cy = handle.center().y();
        QPainterPath chevron;
        if (expanded_) {
            chevron.moveTo(cx - 4, cy + 2); chevron.lineTo(cx, cy - 2); chevron.lineTo(cx + 4, cy + 2);
        } else {
            chevron.moveTo(cx - 4, cy - 2); chevron.lineTo(cx, cy + 2); chevron.lineTo(cx + 4, cy - 2);
        }
        p.setPen(QPen(muted, 1.5, Qt::SolidLine, Qt::RoundCap, Qt::RoundJoin));
        p.drawPath(chevron);
        if (!expanded_) return;

        const QRectF plot(42, 12, std::max(1, width() - 56), height() - kHandleHeight - 28);
        if (plot.height() <= 4) return;
        p.setFont(font());
        p.setPen(QColor(text.red(), text.green(), text.blue(), 75));
        constexpr int lines = 3;
        double peak = 0.0;
        for (const auto& sample : samples_) peak = std::max(peak, sample.second);
        const double scale = peak > 0.0 ? peak * 1.15 : 1.0;
        for (int i = 0; i <= lines; ++i) {
            const qreal y = plot.bottom() - plot.height() * i / lines;
            p.drawLine(QPointF(plot.left(), y), QPointF(plot.right(), y));
            if (i == 0 || i == lines)
                p.drawText(QRectF(2, y - 8, 36, 16), Qt::AlignRight | Qt::AlignVCenter,
                           i == 0 ? QStringLiteral("0") : formatRate(scale));
        }
        p.setPen(QColor(text.red(), text.green(), text.blue(), 48));
        for (int i = 1; i < 4; ++i) {
            const qreal x = plot.left() + plot.width() * i / 4;
            p.drawLine(QPointF(x, plot.top()), QPointF(x, plot.bottom()));
        }
        if (samples_.empty()) return;
        const qint64 end = samples_.back().first;
        const qint64 start = end - kHistoryMs;
        QPainterPath line;
        bool first = true;
        qreal firstX = plot.right();
        for (const auto& sample : samples_) {
            const qreal x = plot.left() + plot.width() * std::clamp(double(sample.first - start) / kHistoryMs, 0.0, 1.0);
            const qreal y = plot.bottom() - plot.height() * std::clamp(sample.second / scale, 0.0, 1.0);
            if (first) { line.moveTo(x, y); firstX = x; first = false; } else line.lineTo(x, y);
        }
        if (samples_.size() == 1) line.lineTo(plot.right(), plot.bottom() - plot.height() * std::clamp(samples_.front().second / scale, 0.0, 1.0));
        QPainterPath fill = line;
        fill.lineTo(plot.right(), plot.bottom()); fill.lineTo(firstX, plot.bottom()); fill.closeSubpath();
        QColor shade = accent; shade.setAlpha(42);
        p.fillPath(fill, shade);
        p.setPen(QPen(accent, 2.0, Qt::SolidLine, Qt::RoundCap, Qt::RoundJoin));
        p.drawPath(line);
    }

private:
    static constexpr int kHandleHeight = 30;
    static constexpr int kExpandedHeight = 180;
    static constexpr qint64 kHistoryMs = 60'000;
    bool expanded_ = false;
    std::deque<std::pair<qint64, double>> samples_;

    static QString formatRate(double bytesPerSecond) {
        double value = std::max(0.0, bytesPerSecond);
        const char* units[] = {"B/s", "KB/s", "MB/s", "GB/s"};
        int unit = 0;
        while (value >= 1024.0 && unit < 3) { value /= 1024.0; ++unit; }
        return QStringLiteral("%1 %2").arg(value, 0, 'f', unit == 0 ? 0 : 1).arg(QString::fromLatin1(units[unit]));
    }
};
