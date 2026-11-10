#pragma once

#include <QMainWindow>
#include <QByteArray>
#include <QPointer>
#include <QString>
#include <QVector>

class QAction;
class QLabel;
class QCheckBox;
class QDialog;
class QProgressBar;
class QPushButton;
class QCloseEvent;
class QMenu;
class QJsonArray;
class QJsonObject;
class QListWidget;
class QStackedWidget;
class DownloadFilter;
class QStandardItemModel;
class QTableView;
class QTableWidget;
class SpeedGraph;
class OptionsDialog;
class TorBoxDialog;
class SegmentMap;

struct DownloadRow
{
    QString id;
    QString fileName;
    QString queue;
    QString size;
    QString status;
    QString timeLeft;
    QString transferRate;
    QString lastTryDate;
    QString description;
    int progress = -1;
    bool canPause = false;
    bool canResume = false;
    int queueId = 0;
    QString priority;
    qint64 speedLimit = 0;
    bool terminal = false;
    qint64 sizeBytes = -1;
    qint64 remainingSeconds = -1;
    double bytesPerSecond = -1;
    qint64 startedMilliseconds = 0;
    qint64 receivedBytes = 0;
    int connections = 0;
    bool canCancel = false;
    bool canChangeSpeedLimit = false;
    bool showCompletionDialog = true;
    QString completedFile;
};

class MainWindow final : public QMainWindow
{
    Q_OBJECT

public:
    explicit MainWindow(QWidget *parent = nullptr);

    // Replaces the visible list with a Host-provided snapshot. Empty means the
    // connected Host has no downloads; connection state is managed separately.
    void setDownloads(const QVector<DownloadRow> &rows);
    void setHostConnected(bool connected);

    void setTorBoxAvailable(bool available);
    bool handleToolResult(const QString &command, const QJsonObject &result);
    void showCompletion(const QJsonObject &job);
    QString visibleDetailsJob() const;
    void setDownloadSpeedHistory(const QString &jobId, const QJsonObject &history);
    void setDownloadConnections(const QString &jobId, const QJsonArray &connections, bool available);
    void setDownloadSegments(const QString &jobId, const QJsonArray &saved, const QJsonArray &connections, bool available);
    void setQueues(const QJsonArray &queues);
    void sampleSpeed(double bytesPerSecond);
    void setCommandBusy(bool busy);

protected:
    void closeEvent(QCloseEvent *event) override;

signals:
    void commandRequested(const QString &command, const QString &jobId);

private:
    void updateActions();
    void showDownloadDetails(const QModelIndex &index);
    void refreshDownloadDetails();
    QCheckBox *detailsCompletionDialog_ = nullptr;
    QPushButton *detailsStop_ = nullptr;
    QPushButton *detailsResume_ = nullptr;
    QPushButton *detailsCancel_ = nullptr;
    QDialog *details_ = nullptr;
    QLabel *detailsSummary_ = nullptr;
    QLabel *speedHistoryStatus_ = nullptr;
    SpeedGraph *detailsSpeedGraph_ = nullptr;
    QLabel *connectionDetailsStatus_ = nullptr;
    QTableWidget *connections_ = nullptr;
    SegmentMap *segmentMap_ = nullptr;
    QLabel *segmentStatus_ = nullptr;
    QTableWidget *segments_ = nullptr;
    QProgressBar *detailsProgress_ = nullptr;
    QString detailsJobId_;
    QAction *openFile_ = nullptr;
    QAction *openFolder_ = nullptr;
    QAction *showDetails_ = nullptr;
    QByteArray defaultColumns_;
    QMenu *priorityMenu_ = nullptr;
    QMenu *moveQueueMenu_ = nullptr;
    QAction *optionsAction_ = nullptr;
    QAction *torboxAction_ = nullptr;
    QPointer<OptionsDialog> optionsDialog_;
    QPointer<TorBoxDialog> torboxDialog_;
    bool torboxAvailable_ = false;
    QAction *add_ = nullptr;
    QAction *resume_ = nullptr;
    QAction *stop_ = nullptr;
    QAction *stopAll_ = nullptr;
    void editQueue(bool create);
    void sendQueueEdit(const QJsonObject &payload);
    QAction *queueNew_ = nullptr;
    QAction *queueEdit_ = nullptr;
    QAction *queueUp_ = nullptr;
    QAction *queueDown_ = nullptr;
    QAction *queueDelete_ = nullptr;
    QAction *queueStart_ = nullptr;
    QAction *queueStop_ = nullptr;
    QAction *deleteCompleted_ = nullptr;
    QAction *editSpeedLimit_ = nullptr;
    QAction *editDescription_ = nullptr;
    QAction *remove_ = nullptr;
    bool commandBusy_ = false;

    QStandardItemModel *model_ = nullptr;
    QLabel *listHeading_ = nullptr;
    QLabel *connectionLabel_ = nullptr;
    QLabel *emptyLabel_ = nullptr;
    QLabel *statusCount_ = nullptr;
    SpeedGraph *speedGraph_ = nullptr;
    QListWidget *queues_ = nullptr;
    QTableView *table_ = nullptr;

    DownloadFilter *filter_ = nullptr;
    QStackedWidget *content_ = nullptr;
    bool connected_ = false;

    void updateEmptyState();
};
