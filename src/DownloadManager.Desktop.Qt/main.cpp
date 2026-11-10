#include "mainwindow.h"

#include <QApplication>
#include <QDir>
#include <QDateTime>
#include <QtMath>
#include <cmath>
#include <QFileInfo>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLocale>
#include <QMap>
#include <QMessageBox>
#include <QProcess>
#include <QTimer>

int main(int argc, char *argv[])
{
    QApplication app(argc, argv);
    app.setApplicationName("Inbound Download Manager");
    app.setOrganizationName("Inbound");

    MainWindow window;
    QProcess reader;
    QProcess command;
    QProcess segmentQuery;
    QTimer segmentPoll;
    QString segmentJob;
    QTimer speedSampler;
    bool connected = false;
    bool defaultCompletionDialog = true;
    QTimer reconnect;
    QTimer repaint;
    QMap<QString, QJsonObject> jobs;
    const auto program = QDir(QCoreApplication::applicationDirPath()).filePath(
#ifdef Q_OS_WIN
        "DownloadManager.Desktop.Qt.HostReader.exe"
#else
        "DownloadManager.Desktop.Qt.HostReader"
#endif
    );
    reader.setProgram(program);
    command.setProgram(program);
    segmentQuery.setProgram(program);
    QObject::connect(&segmentPoll, &QTimer::timeout, &window, [&] {
        if (segmentQuery.state() != QProcess::NotRunning) return;
        segmentJob = window.visibleDetailsJob();
        if (segmentJob.isEmpty()) return;
        segmentQuery.setArguments({"download-telemetry", segmentJob});
        segmentQuery.start();
    });
    QObject::connect(&segmentQuery, &QProcess::finished, &window, [&](int code, QProcess::ExitStatus status) {
        const auto response = QJsonDocument::fromJson(segmentQuery.readAllStandardOutput());
        const auto data = code == 0 && status == QProcess::NormalExit ? response.object() : QJsonObject{};
        const auto segments = data.value("segments");
        const auto connections = data.value("connections");
        window.setDownloadSegments(segmentJob, segments.toArray(), connections.toArray(), segments.isArray());
        window.setDownloadConnections(segmentJob, connections.toArray(), connections.isArray());
        window.setDownloadSpeedHistory(segmentJob, data.value("history").toObject());
    });
    QObject::connect(&segmentQuery, &QProcess::errorOccurred, &window, [&](QProcess::ProcessError error) {
        if (error == QProcess::FailedToStart) {
            window.setDownloadSegments(segmentJob, {}, {}, false);
            window.setDownloadConnections(segmentJob, {}, false);
            window.setDownloadSpeedHistory(segmentJob, {});
        }
    });
    segmentPoll.start(2000);
    QObject::connect(&window, &MainWindow::commandRequested, &window, [&](const QString &name, const QString &id) {
        if (!connected || command.state() != QProcess::NotRunning) return;
        QStringList args{name};
        const bool hasPayload = name == "add" || name == "move-to-queue" || name == "delete-completed" || name == "set-priority" ||
                                name == "edit-description" || name == "edit-speed-limit" ||
                                name == "edit-completion-dialog" || name == "edit-queue" ||
                                name == "options-save" || name.startsWith(QStringLiteral("torbox-"));
        if (!hasPayload && !id.isEmpty()) args.append(id);
        window.setCommandBusy(true);
        command.setArguments(args);
        command.start();
        if (hasPayload) command.write(id.toUtf8() + '\n');
        command.closeWriteChannel();
    });
    QObject::connect(&command, &QProcess::finished, &window, [&](int code, QProcess::ExitStatus status) {
        const auto response = QJsonDocument::fromJson(command.readAllStandardOutput());
        const auto result = response.object();
        if (status == QProcess::NormalExit && result.value("queues").isArray())
            window.setQueues(result.value("queues").toArray());
        window.setCommandBusy(false);
        const auto name = command.arguments().value(0);
        if (code == 0 && name == "options-save") {
            defaultCompletionDialog = result.value("options").toObject().value("showCompletionDialog")
                .toBool(defaultCompletionDialog);
            repaint.start();
        }
        if (window.handleToolResult(name, result)) return;
        if (code != 0 || status != QProcess::NormalExit) {
            const auto message = result.contains("total")
                ? QObject::tr("The Host confirmed %1 of %2 changes.")
                    .arg(result.value("confirmed").toInt()).arg(result.value("total").toInt())
                : result.value("error").toString(QObject::tr("The Host did not confirm the requested action."));
            QMessageBox::warning(&window, QObject::tr("Download action"), message);
        }
    });
    QObject::connect(&command, &QProcess::errorOccurred, &window, [&](QProcess::ProcessError error) {
        if (error != QProcess::FailedToStart) return;
        window.setCommandBusy(false);
        const auto message = QObject::tr("Could not start the Host helper.");
        if (!window.handleToolResult(command.arguments().value(0), {{"ok", false}, {"error", message}}))
            QMessageBox::warning(&window, QObject::tr("Download action"), message);
    });
    QObject::connect(&speedSampler, &QTimer::timeout, &window, [&] {
        double speed = 0;
        if (connected)
            for (const auto &job : std::as_const(jobs))
                if (job.value("state").toString() == "downloading") speed += job.value("rate").toDouble();
        window.sampleSpeed(speed);
    });
    speedSampler.start(1000);
    reconnect.setInterval(2000);
    repaint.setInterval(250);
    repaint.setSingleShot(true);
    const auto start = [&] {
        if (reader.state() == QProcess::NotRunning && QFileInfo(program).isExecutable())
            reader.start();
    };
    QObject::connect(&reconnect, &QTimer::timeout, &window, start);
    QObject::connect(&reader, &QProcess::finished, &window, [&] {
        connected = false;
        window.setHostConnected(false);
    });
    QObject::connect(&repaint, &QTimer::timeout, &window, [&] {
        QVector<DownloadRow> rows;
        rows.reserve(jobs.size());
        const QLocale locale;
        for (const auto &job : std::as_const(jobs)) {
            const auto size = job.value("size");
            const auto rate = job.value("rate");
            const auto state = job.value("state").toString();
            const auto progress = job.value("progress");
            const auto remaining = job.value("remainingSeconds");
            const auto started = QDateTime::fromString(job.value("startedAt").toString(), Qt::ISODate);
            QString eta;
            qint64 seconds = -1;
            if (remaining.isDouble() && remaining.toDouble() >= 0 && state == "downloading") {
                seconds = static_cast<qint64>(std::ceil(remaining.toDouble()));
                eta = seconds >= 60 ? QStringLiteral("%1m %2s").arg(seconds / 60).arg(seconds % 60)
                                    : QStringLiteral("%1s").arg(seconds);
            }
            QString status = state;
            if (status == "retryWaiting") status = QStringLiteral("Retry waiting");
            else if (!status.isEmpty()) status[0] = status[0].toUpper();
            rows.append(DownloadRow{
                job.value("id").toString(), job.value("name").toString(), job.value("queue").toString(),
                size.isDouble() ? locale.formattedDataSize(size.toInteger()) : QString(),
                status, eta,
                rate.isDouble() && rate.toDouble() > 0 ? locale.formattedDataSize(rate.toInteger()) + "/s" : QString(),
                started.isValid() ? locale.toString(started.toLocalTime(), QLocale::ShortFormat) : QString(),
                job.value("description").toString(),
                progress.isDouble() && (state == "downloading" || state == "paused")
                    ? qRound(qBound(0.0, progress.toDouble(), 1.0) * 100) : -1,
                job.value("canPause").toBool(), job.value("canResume").toBool(), job.value("queueId").toInt(),
                job.value("priority").toString(), job.value("speedLimit").toInteger(),
                job.value("terminal").toBool(), size.toInteger(-1), seconds,
                rate.toDouble(-1), started.isValid() ? started.toMSecsSinceEpoch() : 0,
                job.value("received").toInteger(), job.value("connections").toInt(),
                job.value("canCancel").toBool(), job.value("canChangeSpeedLimit").toBool(),
                job.value("showCompletionDialog").toBool(defaultCompletionDialog),
                job.value("completedFile").toString()});
        }
        window.setDownloads(rows);
    });
    QObject::connect(&reader, &QProcess::readyReadStandardOutput, &window, [&] {
        while (reader.canReadLine()) {
            const auto message = QJsonDocument::fromJson(reader.readLine()).object();
            const auto type = message.value("type").toString();
            if (type == "snapshot") {
                jobs.clear();
                defaultCompletionDialog = message.value("defaultCompletionDialog").toBool(true);
                window.setQueues(message.value("queues").toArray());
                window.setTorBoxAvailable(message.value("torboxAvailable").toBool());
                for (const auto value : message.value("jobs").toArray()) {
                    const auto job = value.toObject();
                    jobs.insert(job.value("id").toString(), job);
                }
                connected = true;
                window.setHostConnected(true);
            } else if (type == "update" || type == "remove") {
                const auto job = message.value("job").toObject();
                const auto id = job.value("id").toString();
                if (id.isEmpty()) continue;
                if (type == "remove") jobs.remove(id);
                else {
                    const auto previous = jobs.constFind(id);
                    const bool justCompleted = previous != jobs.cend() &&
                        previous->value("state").toString() != "completed" &&
                        job.value("state").toString() == "completed";
                    jobs.insert(id, job);
                    if (justCompleted && job.value("qtOwned").toBool() &&
                        job.value("showCompletionDialog").toBool(defaultCompletionDialog))
                        window.showCompletion(job);
                }
            } else continue;
            if (!repaint.isActive()) repaint.start();
        }
    });

    window.show();
    start();
    reconnect.start();
    const int result = app.exec();
    if (reader.state() != QProcess::NotRunning) {
        reader.kill();
        reader.waitForFinished(1000);
    }
    if (segmentQuery.state() != QProcess::NotRunning) {
        segmentQuery.kill();
        segmentQuery.waitForFinished(1000);
    }
    return result;
}
