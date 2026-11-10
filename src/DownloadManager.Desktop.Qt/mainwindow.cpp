#include "mainwindow.h"
#include "optionsdialog.h"
#include "torboxdialog.h"
#include <algorithm>
#include <limits>
#include "speedgraph.h"

#include <QAbstractItemView>
#include <QFrame>
#include <QHeaderView>
#include <QLabel>
#include <QStandardItemModel>
#include <QTableView>
#include <QTableWidget>
#include <QTabWidget>
#include <QInputDialog>
#include <QFormLayout>
#include <QComboBox>
#include <QSpinBox>
#include <QDialog>
#include <QDialogButtonBox>
#include <QProgressBar>
#include <QPushButton>
#include <QLocale>
#include <QCloseEvent>
#include <QDateTime>
#include <QDesktopServices>
#include <QFileInfo>
#include <QCheckBox>
#include <QSettings>
#include <QJsonArray>
#include <QJsonDocument>
#include <QMenu>
#include <QJsonObject>
#include <QSignalBlocker>
#include <QIcon>
#include <QUrl>
#include <QLineEdit>
#include <QMenuBar>
#include <QMessageBox>
#include <QToolBar>
#include <QToolButton>
#include <QListWidget>
#include <QSet>
#include <QSortFilterProxyModel>
#include <QStackedWidget>
#include <QPainter>
#include <QStyleHints>
#include <QApplication>
#include <QStyledItemDelegate>
#include <QVBoxLayout>
#include <QHBoxLayout>
#include <QWidget>

enum { JobIdRole = Qt::UserRole + 1, ProgressRole, CanPauseRole, CanResumeRole, QueueIdRole, QueueRunningRole, PriorityRole, SpeedLimitRole, TerminalRole, SortValueRole, ReceivedRole, ConnectionsRole, CanCancelRole, CanChangeSpeedLimitRole, CompletionDialogRole, CompletedFileRole, QueueConcurrencyRole, DefaultQueueRole, QueueQuotaRole };

static void openCompletedPath(QWidget *parent, const QString &path, bool folder)
{
    const QFileInfo file(path);
    const QFileInfo target(folder ? file.absolutePath() : path);
    if (file.isAbsolute() && (folder ? target.isDir() : target.isFile()) &&
        QDesktopServices::openUrl(QUrl::fromLocalFile(target.absoluteFilePath())))
        return;
    QMessageBox::warning(parent, folder ? QObject::tr("Open Folder") : QObject::tr("Open File"),
                         folder ? QObject::tr("Could not open the download folder.")
                                : QObject::tr("Could not open the downloaded file."));
}

class SegmentMap final : public QWidget
{
public:
    explicit SegmentMap(QWidget *parent) : QWidget(parent)
    {
        setFixedHeight(16);
        setAccessibleName(tr("Download byte ranges"));
        hide();
    }

    void setSegments(const QJsonArray &segments)
    {
        segments_ = segments;
        setVisible(!segments_.isEmpty());
        update();
    }

    void setTransfer(qint64 total, qint64 received)
    {
        total_ = total;
        received_ = received;
        update();
    }

    void reset()
    {
        highWater_.clear();
        total_ = received_ = 0;
        setSegments({});
    }

    QJsonArray mergeProgress(QJsonArray saved, const QJsonArray &connections)
    {
        if (saved.isEmpty() && total_ > 0)
            saved.append(QJsonObject{{"start", 0}, {"end", total_ - 1}, {"completed", received_}});
        QJsonArray progress;
        for (const auto value : saved) {
            auto segment = value.toObject();
            const auto start = segment.value("start").toInteger(-1);
            const auto end = segment.value("end").toInteger(-1);
            if (start < 0 || end < start || end == std::numeric_limits<qint64>::max()) continue;
            const auto length = end - start + 1;
            auto completed = std::clamp(segment.value("completed").toInteger(), qint64(0), length);
            for (const auto current : connections) {
                const auto connection = current.toObject();
                const auto from = connection.value("rangeStart").toInteger(-1);
                if (from < start || from > end) continue;
                const auto count = std::clamp(connection.value("receivedBytes").toInteger(),
                                              qint64(0), end - from + 1);
                completed = std::max(completed, from - start + count);
            }
            const auto key = qMakePair(start, end);
            completed = std::max(completed, highWater_.value(key));
            highWater_.insert(key, completed);
            segment.insert("completed", completed);
            segment.insert("fraction", double(completed) / double(length));
            progress.append(segment);
        }
        return progress;
    }

protected:
    void paintEvent(QPaintEvent *) override
    {
        long double extent = total_;
        for (const auto value : segments_) {
            const auto end = value.toObject().value("end").toInteger(-1);
            extent = std::max(extent, static_cast<long double>(end) + 1);
        }
        if (extent <= 0) return;

        QPainter painter(this);
        painter.fillRect(rect(), palette().color(QPalette::AlternateBase));
        const auto x = [this, extent](long double byte) {
            return static_cast<qreal>(width() * std::clamp(byte / extent, 0.0L, 1.0L));
        };
        for (const auto value : segments_) {
            const auto segment = value.toObject();
            const auto start = segment.value("start").toInteger(-1);
            const auto end = segment.value("end").toInteger(-1);
            if (start < 0 || end < start || end == std::numeric_limits<qint64>::max()) continue;
            const auto afterEnd = static_cast<long double>(end) + 1;
            const auto completed = std::max<qint64>(0, segment.value("completed").toInteger());
            const qreal left = x(start);
            const qreal right = x(std::min(afterEnd, static_cast<long double>(start) + completed));
            painter.fillRect(QRectF(left, 0, right - left, height()),
                             palette().color(QPalette::HighlightedText));
            if (start != 0) {
                painter.setPen(palette().color(QPalette::Mid));
                painter.drawLine(QPointF(left, 0), QPointF(left, height()));
            }
        }
    }

private:
    QJsonArray segments_;
    qint64 total_ = 0;
    qint64 received_ = 0;
    QHash<QPair<qint64, qint64>, qint64> highWater_;
};

class DownloadFilter final : public QSortFilterProxyModel
{
public:
    using QSortFilterProxyModel::QSortFilterProxyModel;

    void setView(int view) { view_ = view; invalidateFilter(); }
    void setQueue(const QVariant &queue) { queue_ = queue; invalidateFilter(); }
    void setSearch(const QString &search) { search_ = search.trimmed(); invalidateFilter(); }

protected:
    bool filterAcceptsRow(int row, const QModelIndex &parent) const override
    {
        const auto value = [&](int column) {
            return sourceModel()->index(row, column, parent).data().toString();
        };
        if (!value(0).contains(search_, Qt::CaseInsensitive) &&
            !value(7).contains(search_, Qt::CaseInsensitive))
            return false;

        if (queue_.isValid() && sourceModel()->index(row, 0, parent).data(QueueIdRole) != queue_) return false;
        const auto state = value(3).toLower();
        switch (view_) {
        case 1: return state == "downloading" || state == "connecting" || state == "verifying";
        case 2: return state == "queued";
        case 3: return state == "paused";
        case 4: return state == "failed";
        case 5: return state == "completed";
        case 6: return state != "completed" && state != "cancelled";
        default: return true;
        }
    }

private:
    int view_ = 0;
    QString search_;
    QVariant queue_;
};

namespace {
class StatusDelegate final : public QStyledItemDelegate
{
public:
    using QStyledItemDelegate::QStyledItemDelegate;

    void paint(QPainter *painter, const QStyleOptionViewItem &option,
               const QModelIndex &index) const override
    {
        const int progress = index.data(ProgressRole).toInt();
        if (progress < 0) {
            QStyledItemDelegate::paint(painter, option, index);
            return;
        }

        QStyleOptionViewItem background(option);
        initStyleOption(&background, index);
        background.text.clear();
        const auto *style = option.widget->style();
        style->drawControl(QStyle::CE_ItemViewItem, &background, painter, option.widget);
        painter->save();
        const bool selected = option.state.testFlag(QStyle::State_Selected);
        const auto ink = option.palette.color(selected ? QPalette::HighlightedText : QPalette::Text);
        painter->setPen(ink);
        const QRect label = option.rect.adjusted(8, 2, -8, -7);
        painter->drawText(label, Qt::AlignVCenter | Qt::AlignLeft,
            option.fontMetrics.elidedText(QStringLiteral("%1 %2%").arg(index.data().toString()).arg(progress),
                                          Qt::ElideRight, label.width()));
        const QRect track(option.rect.left() + 8, option.rect.bottom() - 6,
                          qMax(0, qMin(108, option.rect.width() - 16)), 3);
        QColor trackColor = ink;
        trackColor.setAlpha(55);
        painter->fillRect(track, trackColor);
        const QColor fill = selected ? ink : option.palette.color(QPalette::HighlightedText);
        painter->fillRect(QRect(track.topLeft(), QSize(track.width() * progress / 100, track.height())), fill);
        painter->restore();
    }
};

const QStringList columns = {
    QStringLiteral("File Name"), QStringLiteral("Q"), QStringLiteral("Size"),
    QStringLiteral("Status"), QStringLiteral("Time left"),
    QStringLiteral("Transfer rate"), QStringLiteral("Started"),
    QStringLiteral("Description")
};
}

MainWindow::MainWindow(QWidget *parent) : QMainWindow(parent)
{
    setWindowTitle(QStringLiteral("Inbound Download Manager"));
    resize(1140, 650);

    auto *tasks = menuBar()->addMenu(tr("&Tasks"));
    auto *downloads = menuBar()->addMenu(tr("&Downloads"));
    auto *viewMenu = menuBar()->addMenu(tr("&View"));
    tasks->addAction(tr("Close window"), this, &QWidget::close);
    auto *toolbar = addToolBar(tr("Download controls"));
    toolbar->setMovable(false);
    toolbar->setIconSize(QSize(16, 16));
    toolbar->setToolButtonStyle(Qt::ToolButtonTextBesideIcon);
    const auto action = [&](QStyle::StandardPixmap icon, const QString &label, const QString &command) {
        auto *item = new QAction(style()->standardIcon(icon), label, this);
        connect(item, &QAction::triggered, this, [this, command] {
            const auto selected = table_->selectionModel()->selectedRows();
            if (command != "stop-all" && selected.size() != 1) return;
            if (command == "remove" &&
                QMessageBox::question(this, tr("Remove download"), tr("Remove this entry from the list? The file will be kept."),
                                      QMessageBox::Yes | QMessageBox::No, QMessageBox::No) != QMessageBox::Yes)
                return;
            emit commandRequested(command, command == "stop-all" ? QString() : selected.front().data(JobIdRole).toString());
        });
        toolbar->addAction(item);
        downloads->addAction(item);
        return item;
    };
    add_ = new QAction(style()->standardIcon(QStyle::SP_FileDialogNewFolder), tr("Add URL"), this);
    add_->setIcon(QIcon::fromTheme(QStringLiteral("list-add"), style()->standardIcon(QStyle::SP_ArrowDown)));
    toolbar->addAction(add_);
    tasks->insertAction(tasks->actions().front(), add_);
    connect(add_, &QAction::triggered, this, [this] {
        bool accepted = false;
        const auto url = QInputDialog::getText(this, tr("Add URL"), tr("Download URL"),
                                               QLineEdit::Normal, {}, &accepted).trimmed();
        if (!accepted || url.isEmpty()) return;
        const QUrl parsed(url, QUrl::StrictMode);
        if (!parsed.isValid() || parsed.host().isEmpty() ||
            (parsed.scheme() != "http" && parsed.scheme() != "https")) {
            QMessageBox::warning(this, tr("Add URL"), tr("Enter a valid HTTP or HTTPS URL."));
            return;
        }
        emit commandRequested("add", url);
    });
    toolbar->addSeparator();
    resume_ = action(QStyle::SP_MediaPlay, tr("Resume"), "resume");
    stop_ = action(QStyle::SP_MediaPause, tr("Stop"), "stop");
    stopAll_ = action(QStyle::SP_MediaStop, tr("Stop All"), "stop-all");
    toolbar->addSeparator();
    remove_ = action(QStyle::SP_TrashIcon, tr("Remove Entry"), "remove");
    deleteCompleted_ = toolbar->addAction(style()->standardIcon(QStyle::SP_DialogDiscardButton),
                                          tr("Delete Completed"));
    downloads->addAction(deleteCompleted_);
    connect(deleteCompleted_, &QAction::triggered, this, [this] {
        QJsonArray completed;
        for (int row = 0; row < model_->rowCount(); ++row) {
            if (model_->item(row, 3)->text() == QStringLiteral("Completed"))
                completed.append(model_->item(row, 0)->data(JobIdRole).toString());
        }
        if (completed.isEmpty()) return;
        if (QMessageBox::question(this, tr("Delete completed downloads?"),
                tr("Remove %n completed download(s) from the list? Downloaded files will stay on disk.",
                   nullptr, completed.size()), QMessageBox::Yes | QMessageBox::No, QMessageBox::No) != QMessageBox::Yes)
            return;
        emit commandRequested("delete-completed",
            QString::fromUtf8(QJsonDocument(completed).toJson(QJsonDocument::Compact)));
    });

    toolbar->addSeparator();
    auto *queueMenu = menuBar()->addMenu(tr("&Queues"));
    const auto queueAction = [&](QStyle::StandardPixmap icon, const QString &title, const QString &command) {
        auto *item = new QAction(style()->standardIcon(icon), title, this);
        connect(item, &QAction::triggered, this, [this, command] {
            if (auto *queue = queues_->currentItem())
                emit commandRequested(command, queue->data(QueueIdRole).toString());
        });
        toolbar->addAction(item);
        queueMenu->addAction(item);
        return item;
    };
    queueStart_ = queueAction(QStyle::SP_MediaPlay, tr("Start Queue"), "queue-start");
    queueStop_ = queueAction(QStyle::SP_MediaStop, tr("Stop Queue"), "queue-stop");

    queueMenu->addSeparator();
    queueNew_ = queueMenu->addAction(tr("New Queue…"), this, [this] { editQueue(true); });
    queueEdit_ = queueMenu->addAction(tr("Edit Queue…"), this, [this] { editQueue(false); });
    const auto queueEditAction = [this, queueMenu](const QString &label, const QString &action) {
        return queueMenu->addAction(label, this, [this, action] {
            const auto *queue = queues_->currentItem();
            if (!queue) return;
            const int id = queue->data(QueueIdRole).toInt();
            sendQueueEdit(QJsonObject{{"action", action}, {"queueId", id}});
        });
    };
    queueUp_ = queueEditAction(tr("Move Queue Up"), "moveup");
    queueDown_ = queueEditAction(tr("Move Queue Down"), "movedown");
    queueDelete_ = queueMenu->addAction(tr("Delete Queue…"), this, [this] {
        const auto *source = queues_->currentItem();
        if (!source || source->data(DefaultQueueRole).toBool()) return;
        const int id = source->data(QueueIdRole).toInt();
        QDialog dialog(this);
        dialog.setWindowTitle(tr("Delete Queue"));
        auto *form = new QFormLayout(&dialog);
        auto *message = new QLabel(tr("Delete %1? Choose where to move its downloads and schedules.")
                                       .arg(source->text()), &dialog);
        message->setTextFormat(Qt::PlainText);
        message->setWordWrap(true);
        form->addRow(message);
        auto *destination = new QComboBox(&dialog);
        destination->addItem(tr("Delete only if empty"));
        for (int row = 0; row < queues_->count(); ++row) {
            const auto *queue = queues_->item(row);
            const auto queueId = queue->data(QueueIdRole);
            if (queueId.toInt() != id) destination->addItem(queue->text(), queueId);
        }
        form->addRow(tr("Move to"), destination);
        auto *buttons = new QDialogButtonBox(QDialogButtonBox::Cancel, &dialog);
        auto *remove = buttons->addButton(tr("Delete Queue"), QDialogButtonBox::AcceptRole);
        remove->setAutoDefault(false);
        buttons->button(QDialogButtonBox::Cancel)->setDefault(true);
        connect(buttons, &QDialogButtonBox::accepted, &dialog, &QDialog::accept);
        connect(buttons, &QDialogButtonBox::rejected, &dialog, &QDialog::reject);
        form->addRow(buttons);
        if (dialog.exec() != QDialog::Accepted) return;
        QJsonObject request{{"action", "delete"}, {"queueId", id}};
        const auto target = destination->currentData();
        if (target.isValid()) request.insert("moveContentsToQueueId", target.toInt());
        sendQueueEdit(request);
    });

    moveQueueMenu_ = downloads->addMenu(tr("Move to Queue"));
    connect(moveQueueMenu_, &QMenu::aboutToShow, this, [this] {
        moveQueueMenu_->clear();
        const auto selection = table_->selectionModel()->selectedRows();
        QJsonArray jobs;
        for (const auto &row : selection) jobs.append(row.data(JobIdRole).toString());
        const auto addDestination = [this, jobs](const QString &label, const QJsonValue &id) {
            auto *action = moveQueueMenu_->addAction(label);
            connect(action, &QAction::triggered, this, [this, jobs, id] {
                const QJsonObject payload{{"queueId", id}, {"jobIds", jobs}};
                emit commandRequested("move-to-queue",
                    QString::fromUtf8(QJsonDocument(payload).toJson(QJsonDocument::Compact)));
            });
        };
        for (int row = 0; row < queues_->count(); ++row) {
            const auto *queue = queues_->item(row);
            addDestination(queue->text(), queue->data(QueueIdRole).toInt());
        }
        moveQueueMenu_->addSeparator();
        addDestination(tr("No Queue"), QJsonValue::Null);
    });

    priorityMenu_ = downloads->addMenu(tr("Priority"));
    connect(priorityMenu_, &QMenu::aboutToShow, this, [this] {
        priorityMenu_->clear();
        QJsonArray jobs;
        QSet<QString> currentPriorities;
        for (const auto &row : table_->selectionModel()->selectedRows()) {
            if (row.siblingAtColumn(3).data().toString() == QStringLiteral("Completed")) continue;
            jobs.append(row.data(JobIdRole).toString());
            currentPriorities.insert(row.data(PriorityRole).toString());
        }
        const auto addPriority = [this, jobs, currentPriorities](const QString &label, const QString &value) {
            auto *action = priorityMenu_->addAction(label);
            action->setCheckable(true);
            action->setChecked(currentPriorities.size() == 1 && currentPriorities.contains(value));
            action->setEnabled(!jobs.isEmpty());
            connect(action, &QAction::triggered, this, [this, jobs, value] {
                const QJsonObject payload{{"priority", value}, {"jobIds", jobs}};
                emit commandRequested("set-priority",
                    QString::fromUtf8(QJsonDocument(payload).toJson(QJsonDocument::Compact)));
            });
        };
        addPriority(tr("High"), QStringLiteral("high"));
        addPriority(tr("Normal"), QStringLiteral("normal"));
        addPriority(tr("Low"), QStringLiteral("low"));
    });

    editDescription_ = downloads->addAction(tr("Edit Description…"), this, [this] {
        const auto selected = table_->selectionModel()->selectedRows();
        if (selected.size() != 1) return;
        const auto row = selected.front();
        const auto jobId = row.data(JobIdRole).toString();
        const auto original = row.siblingAtColumn(7).data().toString();
        bool accepted = false;
        const auto description = QInputDialog::getMultiLineText(this, tr("Edit Description"),
            tr("Description for %1").arg(row.data().toString()), original, &accepted);
        if (!accepted || description == original) return;
        const QJsonObject payload{{"jobId", jobId}, {"description", description}};
        emit commandRequested("edit-description",
            QString::fromUtf8(QJsonDocument(payload).toJson(QJsonDocument::Compact)));
    });

    editSpeedLimit_ = downloads->addAction(tr("Set Download Speed Limit…"), this, [this] {
        const auto selected = table_->selectionModel()->selectedRows();
        if (selected.size() != 1 || !selected.front().data(CanChangeSpeedLimitRole).toBool()) return;
        const auto row = selected.front();
        const auto id = row.data(JobIdRole).toString();
        const auto current = row.data(SpeedLimitRole).toLongLong();
        bool accepted = false;
        const auto input = QInputDialog::getText(this, tr("Download Speed Limit"),
            tr("Bytes per second for %1 (0 for unlimited)").arg(row.data().toString()),
            QLineEdit::Normal, QString::number(current), &accepted);
        if (!accepted) return;
        bool valid = false;
        const auto limit = input.trimmed().toLongLong(&valid);
        if (!valid || limit < 0) {
            QMessageBox::warning(this, tr("Download Speed Limit"), tr("Enter zero or a positive whole number."));
            return;
        }
        if (limit == current) return;
        const QJsonObject payload{{"jobId", id}, {"speedLimitBytesPerSecond", limit}};
        emit commandRequested("edit-speed-limit",
            QString::fromUtf8(QJsonDocument(payload).toJson(QJsonDocument::Compact)));
    });

    const auto openSelection = [this](bool folder) {
        const auto selection = table_->selectionModel()->selectedRows();
        if (selection.size() != 1) return;
        const auto path = selection.front().data(CompletedFileRole).toString();
        if (!path.isEmpty()) openCompletedPath(this, path, folder);
    };
    openFile_ = downloads->addAction(tr("Open File"), this, [openSelection] { openSelection(false); });
    openFolder_ = downloads->addAction(tr("Open Folder"), this, [openSelection] { openSelection(true); });
    showDetails_ = downloads->addAction(tr("Download Details…"), this, [this] {
        const auto selected = table_->selectionModel()->selectedRows();
        if (selected.size() == 1) showDownloadDetails(selected.front());
    });

    optionsAction_ = tasks->addAction(tr("Options…"), this, [this] {
        if (optionsDialog_) {
            optionsDialog_->raise();
            optionsDialog_->activateWindow();
        } else emit commandRequested("options-get", {});
    });
    toolbar->addSeparator();
    toolbar->addAction(optionsAction_);
    torboxAction_ = tasks->addAction(tr("TorBox…"), this, [this] {
        if (!torboxDialog_) {
            torboxDialog_ = new TorBoxDialog(this, [this](const QString &command, const QJsonObject &payload) {
                if (!connected_ || commandBusy_) {
                    torboxDialog_->handleResult(command, {{"ok", false}, {"error", tr("Host unavailable or busy.")}});
                    return;
                }
                emit commandRequested(command, QString::fromUtf8(QJsonDocument(payload).toJson(QJsonDocument::Compact)));
            });
            torboxDialog_->setAttribute(Qt::WA_DeleteOnClose);
        }
        torboxDialog_->show();
        torboxDialog_->raise();
        torboxDialog_->activateWindow();
    });
    toolbar->addAction(torboxAction_);

    auto *root = new QWidget(this);
    auto *outer = new QVBoxLayout(root);
    outer->setContentsMargins(0, 0, 0, 0);
    outer->setSpacing(0);

    auto *body = new QWidget(root);
    auto *bodyLayout = new QHBoxLayout(body);
    bodyLayout->setContentsMargins(0, 0, 0, 0);
    bodyLayout->setSpacing(0);

    auto *sidebar = new QFrame(body);
    sidebar->setObjectName(QStringLiteral("sidebar"));
    sidebar->setFixedWidth(156);
    auto *sideLayout = new QVBoxLayout(sidebar);
    sideLayout->setContentsMargins(8, 8, 8, 8);
    sideLayout->setSpacing(4);
    auto *section = new QLabel(QStringLiteral("DOWNLOADS"), sidebar);
    sideLayout->addWidget(section);
    auto *views = new QListWidget(sidebar);
    views->setObjectName(QStringLiteral("downloadViews"));
    views->setAccessibleName(QStringLiteral("Download views"));
    views->setFrameShape(QFrame::NoFrame);
    views->setVerticalScrollBarPolicy(Qt::ScrollBarAlwaysOff);
    views->addItems({tr("All Downloads"), tr("Active"), tr("Queued"), tr("Paused"),
                     tr("Failed"), tr("Finished"), tr("Unfinished")});
    views->setCurrentRow(0);
    sideLayout->addWidget(views);
    auto *queueLabel = new QLabel(tr("QUEUES"), sidebar);
    sideLayout->addWidget(queueLabel);
    auto *queues = new QListWidget(sidebar);
    queues->setAccessibleName(tr("Queues"));
    queues->setFrameShape(QFrame::NoFrame);
    queues->setMaximumHeight(130);
    sideLayout->addWidget(queues);
    sideLayout->addStretch();
    connect(queues, &QListWidget::currentItemChanged, this, [this, views](QListWidgetItem *item) {
        if (item) {
            const QSignalBlocker block(views);
            views->setCurrentRow(-1);
            filter_->setView(0);
        }
        filter_->setQueue(item ? item->data(QueueIdRole) : QVariant());
        updateEmptyState();
    });
    queues_ = queues;

    auto *list = new QWidget(body);
    auto *listLayout = new QVBoxLayout(list);
    listLayout->setContentsMargins(0, 0, 0, 0);
    listLayout->setSpacing(0);
    auto *listHeading = new QLabel(QStringLiteral("All Downloads"), list);
    listHeading_ = listHeading;
    QFont headingFont = listHeading->font();
    headingFont.setPointSize(11);
    headingFont.setBold(true);
    listHeading->setFont(headingFont);

    auto *search = new QLineEdit(list);
    search->setPlaceholderText(QStringLiteral("Search downloads…"));
    search->setAccessibleName(QStringLiteral("Search downloads"));
    search->setClearButtonEnabled(true);
    search->setMaximumWidth(235);
    auto *find = viewMenu->addAction(tr("Find Downloads"), search, [search] { search->setFocus(); });
    find->setShortcut(QKeySequence::Find);
    auto *heading = new QHBoxLayout;
    heading->setContentsMargins(10, 6, 8, 5);
    heading->addWidget(listHeading);
    heading->addStretch();
    heading->addWidget(search);
    listLayout->addLayout(heading);

    connect(queues, &QListWidget::currentItemChanged, listHeading, [listHeading](QListWidgetItem *item) {
        if (item) listHeading->setText(item->text());
    });

    content_ = new QStackedWidget(list);
    table_ = new QTableView(list);
    model_ = new QStandardItemModel(0, columns.size(), this);
    model_->setHorizontalHeaderLabels(columns);
    filter_ = new DownloadFilter(this);
    filter_->setSourceModel(model_);
    filter_->setSortRole(SortValueRole);
    filter_->setSortLocaleAware(true);
    filter_->setSortCaseSensitivity(Qt::CaseInsensitive);
    table_->setModel(filter_);
    table_->setItemDelegateForColumn(3, new StatusDelegate(table_));
    connect(search, &QLineEdit::textChanged, this, [this](const QString &text) {
        filter_->setSearch(text);
        updateEmptyState();
    });
    connect(views, &QListWidget::currentRowChanged, this, [this, views, queues, listHeading](int row) {
        if (row < 0) return;
        queues->setCurrentRow(-1);
        listHeading->setText(views->item(row)->text());
        filter_->setView(row);
        updateEmptyState();
    });
    table_->setAlternatingRowColors(true);
    table_->setSelectionBehavior(QAbstractItemView::SelectRows);
    table_->setSelectionMode(QAbstractItemView::ExtendedSelection);
    connect(table_, &QTableView::doubleClicked, this, &MainWindow::showDownloadDetails);
    table_->setContextMenuPolicy(Qt::CustomContextMenu);
    connect(table_, &QTableView::customContextMenuRequested, this, [this](const QPoint &position) {
        const auto hit = table_->indexAt(position);
        if (!hit.isValid()) return;
        if (!table_->selectionModel()->isRowSelected(hit.row()))
            table_->selectRow(hit.row());
        QMenu menu(table_);
        menu.addAction(resume_);
        menu.addAction(stop_);
        menu.addMenu(moveQueueMenu_);
        menu.addMenu(priorityMenu_);
        menu.addAction(openFile_);
        menu.addAction(openFolder_);
        menu.addAction(showDetails_);
        menu.addAction(editDescription_);
        menu.addAction(editSpeedLimit_);
        menu.addSeparator();
        menu.addAction(remove_);
        menu.exec(table_->viewport()->mapToGlobal(position));
    });
    table_->setSortingEnabled(true);
    table_->setEditTriggers(QAbstractItemView::NoEditTriggers);
    table_->verticalHeader()->hide();
    table_->setFrameShape(QFrame::NoFrame);
    table_->setShowGrid(true);
    table_->setGridStyle(Qt::SolidLine);
    connect(table_->selectionModel(), &QItemSelectionModel::selectionChanged, this, [this] { updateActions(); });
    table_->verticalHeader()->setDefaultSectionSize(30);
    table_->horizontalHeader()->setFixedHeight(26);
    table_->horizontalHeader()->setDefaultAlignment(Qt::AlignLeft | Qt::AlignVCenter);
    const int widths[] = {230, 58, 78, 155, 79, 104, 128, 120};
    for (int i = 0; i < columns.size(); ++i)
        table_->setColumnWidth(i, widths[i]);
    table_->horizontalHeader()->setStretchLastSection(true);
    auto *header = table_->horizontalHeader();
    header->setSectionsMovable(true);
    table_->sortByColumn(0, Qt::AscendingOrder);
    defaultColumns_ = header->saveState();
    QSettings preferences;
    const auto columns = preferences.value(QStringLiteral("downloads/columns-v1")).toByteArray();
    if (!columns.isEmpty() && !header->restoreState(columns))
        header->restoreState(defaultColumns_);
    viewMenu->addAction(tr("Reset Download Columns"), this, [this, header] {
        header->restoreState(defaultColumns_);
        table_->sortByColumn(0, Qt::AscendingOrder);
        QSettings().remove(QStringLiteral("downloads/columns-v1"));
    });

    emptyLabel_ = new QLabel(content_);
    emptyLabel_->setAlignment(Qt::AlignCenter);
    content_->addWidget(table_);
    content_->addWidget(emptyLabel_);
    listLayout->addWidget(content_, 1);
    bodyLayout->addWidget(sidebar);
    list->setAutoFillBackground(true);
    list->setBackgroundRole(QPalette::Base);
    bodyLayout->addWidget(list, 1);
    outer->addWidget(body, 1);

    speedGraph_ = new SpeedGraph(root);
    outer->addWidget(speedGraph_);
    auto *showGraph = viewMenu->addAction(tr("Toggle Speed Graph"), this, [this] {
        speedGraph_->setExpanded(!speedGraph_->isExpanded());
    });
    showGraph->setShortcut(QKeySequence(Qt::CTRL | Qt::Key_G));

    auto *status = new QWidget(root);
    status->setObjectName(QStringLiteral("statusBar"));
    auto *statusLayout = new QHBoxLayout(status);
    statusLayout->setContentsMargins(12, 5, 12, 5);
    statusCount_ = new QLabel(QStringLiteral("0 downloads"), status);
    statusLayout->addWidget(statusCount_);
    statusLayout->addStretch();
    connectionLabel_ = new QLabel(status);
    statusLayout->addWidget(connectionLabel_);
    outer->addWidget(status);

    setCentralWidget(root);
    setHostConnected(false);
    const auto applyAppearance = [this, views] {
        const bool dark = QGuiApplication::styleHints()->colorScheme() == Qt::ColorScheme::Dark ||
            (QGuiApplication::styleHints()->colorScheme() == Qt::ColorScheme::Unknown &&
             QApplication::palette().color(QPalette::Window).lightness() < 128);
        const auto color = [dark](const char *light, const char *night) {
            return QString::fromLatin1(dark ? night : light);
        };
        const auto chrome = color("#eff1f4", "#292c31");
        const auto paper = color("#ffffff", "#191c22");
        const auto edge = color("#d9dee8", "#373e4b");
        const auto ink = color("#202635", "#eef1f6");
        const auto selection = color("#e4eeff", "#283c5a");
        const auto accent = color("#2259b1", "#8db9ff");
        QPalette scheme = QApplication::palette();
        scheme.setColor(QPalette::Window, QColor(chrome));
        scheme.setColor(QPalette::Base, QColor(paper));
        scheme.setColor(QPalette::AlternateBase, QColor(chrome));
        scheme.setColor(QPalette::WindowText, QColor(ink));
        scheme.setColor(QPalette::Text, QColor(ink));
        scheme.setColor(QPalette::Button, QColor(chrome));
        scheme.setColor(QPalette::ButtonText, QColor(ink));
        scheme.setColor(QPalette::Highlight, QColor(selection));
        scheme.setColor(QPalette::HighlightedText, QColor(accent));
        scheme.setColor(QPalette::Disabled, QPalette::ButtonText, QColor(ink).lighter(dark ? 65 : 160));
        setPalette(scheme);

        setStyleSheet(QStringLiteral(
            "QMenuBar { background: %1; border-bottom: 1px solid %2; padding: 2px 7px; }"
            "QMenuBar::item { padding: 3px 8px; }"
            "QToolBar { background: %1; border: 0; border-bottom: 1px solid %2; padding: 5px 8px; spacing: 5px; }"
            "QToolBar QToolButton { border: 1px solid %2; border-radius: 2px; padding: 3px 6px; }"
            "QToolBar QToolButton:hover:enabled { background: %3; }"
            "QFrame#sidebar { background: %1; border-right: 1px solid %2; }"
            "QFrame#sidebar QLabel { background: %4; border-bottom: 1px solid %2; padding: 4px 6px; font-size: 11px; }"
            "QFrame#sidebar QListWidget { background: %1; outline: 0; }"
            "QFrame#sidebar QListWidget::item { padding: 5px 7px; border-left: 2px solid transparent; }"
            "QFrame#sidebar QListWidget::item:selected { background: %3; color: %5; border-left-color: %5; }"
            "QTableView { gridline-color: %2; selection-background-color: %3; selection-color: %5; }"
            "QHeaderView::section { background: %1; border: 0; border-right: 1px solid %2; border-bottom: 1px solid %2; padding-left: 7px; }"
            "QWidget#statusBar { border-top: 1px solid %2; }"
        ).arg(chrome, edge, selection, paper, accent));
        views->doItemsLayout();
        views->setFixedHeight(views->sizeHintForRow(0) * views->count() + 10);
    };
    applyAppearance();
    connect(QGuiApplication::styleHints(), &QStyleHints::colorSchemeChanged, this, applyAppearance);
}

void MainWindow::setDownloads(const QVector<DownloadRow> &rows)
{
    QSet<QString> incoming;
    for (const auto &row : rows) incoming.insert(row.id);
    for (int i = model_->rowCount() - 1; i >= 0; --i) {
        if (!incoming.contains(model_->item(i, 0)->data(JobIdRole).toString()))
            model_->removeRow(i);
    }

    QHash<QString, int> existing;
    for (int i = 0; i < model_->rowCount(); ++i)
        existing.insert(model_->item(i, 0)->data(JobIdRole).toString(), i);

    for (const DownloadRow &row : rows) {
        const QStringList values = {row.fileName, row.queue, row.size, row.status,
                                    row.timeLeft, row.transferRate,
                                    row.lastTryDate, row.description};
        const QVariantList sortValues = {row.fileName, row.queue, row.sizeBytes, row.status,
                                         row.remainingSeconds, row.bytesPerSecond,
                                         row.startedMilliseconds, row.description};
        const int index = existing.value(row.id, -1);
        if (index < 0) {
            QList<QStandardItem *> items;
            for (int column = 0; column < values.size(); ++column) {
                auto *item = new QStandardItem(values[column]);
                item->setData(sortValues[column], SortValueRole);
                items.append(item);
            }
            const auto fallback = style()->standardIcon(QStyle::SP_FileIcon);
            items[0]->setIcon(QIcon::fromTheme(QStringLiteral("text-x-generic"), fallback));
            items[0]->setData(row.id, JobIdRole);
            items[0]->setData(row.queueId, QueueIdRole);
            items[0]->setData(row.priority, PriorityRole);
            items[0]->setData(row.speedLimit, SpeedLimitRole);
            items[0]->setData(row.terminal, TerminalRole);
            items[0]->setData(row.receivedBytes, ReceivedRole);
            items[0]->setData(row.connections, ConnectionsRole);
            items[0]->setData(row.canCancel, CanCancelRole);
            items[0]->setData(row.canChangeSpeedLimit, CanChangeSpeedLimitRole);
            items[0]->setData(row.showCompletionDialog, CompletionDialogRole);
            items[0]->setData(row.completedFile, CompletedFileRole);
            items[0]->setData(row.canPause, CanPauseRole);
            items[0]->setData(row.canResume, CanResumeRole);
            items[3]->setData(row.progress, ProgressRole);
            model_->appendRow(items);
        } else {
            for (int column = 0; column < values.size(); ++column) {
                auto *item = model_->item(index, column);
                item->setText(values[column]);
                item->setData(sortValues[column], SortValueRole);
            }
            model_->item(index, 3)->setData(row.progress, ProgressRole);
            model_->item(index, 0)->setData(row.queueId, QueueIdRole);
            model_->item(index, 0)->setData(row.priority, PriorityRole);
            model_->item(index, 0)->setData(row.speedLimit, SpeedLimitRole);
            model_->item(index, 0)->setData(row.terminal, TerminalRole);
            model_->item(index, 0)->setData(row.receivedBytes, ReceivedRole);
            model_->item(index, 0)->setData(row.connections, ConnectionsRole);
            model_->item(index, 0)->setData(row.canCancel, CanCancelRole);
            model_->item(index, 0)->setData(row.canChangeSpeedLimit, CanChangeSpeedLimitRole);
            model_->item(index, 0)->setData(row.showCompletionDialog, CompletionDialogRole);
            model_->item(index, 0)->setData(row.completedFile, CompletedFileRole);
            model_->item(index, 0)->setData(row.canPause, CanPauseRole);
            model_->item(index, 0)->setData(row.canResume, CanResumeRole);
        }
    }
    updateEmptyState();
    refreshDownloadDetails();
}

void MainWindow::setHostConnected(bool connected)
{
    connected_ = connected;
    connectionLabel_->setText(connected ? QStringLiteral("Host connected")
                                        : QStringLiteral("Host disconnected"));
    updateEmptyState();
    refreshDownloadDetails();
}

void MainWindow::updateEmptyState()
{
    const int visible = filter_->rowCount();
    statusCount_->setText(QStringLiteral("%1 of %2 downloads").arg(visible).arg(model_->rowCount()));
    emptyLabel_->setText(!connected_ && model_->rowCount() == 0
        ? QStringLiteral("Waiting for Inbound Host")
        : model_->rowCount() == 0 ? QStringLiteral("No downloads yet")
                                 : QStringLiteral("No downloads match this view"));
    content_->setCurrentWidget(visible ? static_cast<QWidget *>(table_) : emptyLabel_);
    updateActions();
}

void MainWindow::setCommandBusy(bool busy)
{
    commandBusy_ = busy;
    updateActions();
    refreshDownloadDetails();
}

void MainWindow::updateActions()
{
    const auto selection = table_->selectionModel()->selectedRows();
    const bool ready = connected_ && !commandBusy_;
    const bool one = ready && selection.size() == 1;
    add_->setEnabled(ready);
    optionsAction_->setEnabled(ready);
    torboxAction_->setEnabled(ready && torboxAvailable_);
    moveQueueMenu_->setEnabled(ready && !selection.isEmpty());
    priorityMenu_->setEnabled(ready && std::any_of(selection.cbegin(), selection.cend(), [](const auto &row) {
        return row.siblingAtColumn(3).data().toString() != QStringLiteral("Completed");
    }));
    const auto *queue = queues_->currentItem();
    queueNew_->setEnabled(ready);
    queueEdit_->setEnabled(ready && queue);
    queueUp_->setEnabled(ready && queue && queues_->currentRow() > 0);
    queueDown_->setEnabled(ready && queue && queues_->currentRow() + 1 < queues_->count());
    queueDelete_->setEnabled(ready && queue && !queue->data(DefaultQueueRole).toBool());
    queueStart_->setEnabled(ready && queue && !queue->data(QueueRunningRole).toBool());
    queueStop_->setEnabled(ready && queue && queue->data(QueueRunningRole).toBool());
    resume_->setEnabled(one && selection.front().data(CanResumeRole).toBool());
    stop_->setEnabled(one && selection.front().data(CanPauseRole).toBool());
    remove_->setEnabled(one);
    const bool hasFile = selection.size() == 1 && !selection.front().data(CompletedFileRole).toString().isEmpty();
    openFile_->setEnabled(hasFile);
    openFolder_->setEnabled(hasFile);
    showDetails_->setEnabled(selection.size() == 1);
    editDescription_->setEnabled(one);
    editSpeedLimit_->setEnabled(one && selection.front().data(CanChangeSpeedLimitRole).toBool());
    bool anyPausable = false;
    bool anyCompleted = false;
    for (int row = 0; row < model_->rowCount() && !(anyPausable && anyCompleted); ++row) {
        anyPausable |= model_->item(row, 0)->data(CanPauseRole).toBool();
        anyCompleted |= model_->item(row, 3)->text() == QStringLiteral("Completed");
    }
    deleteCompleted_->setEnabled(ready && anyCompleted);
    stopAll_->setEnabled(ready && anyPausable);
}


void MainWindow::sampleSpeed(double bytesPerSecond)
{
    speedGraph_->addSample(bytesPerSecond);
}

void MainWindow::setQueues(const QJsonArray &queues)
{
    const auto selected = queues_->currentItem() ? queues_->currentItem()->data(QueueIdRole) : QVariant();
    const QSignalBlocker block(queues_);
    queues_->clear();
    for (const auto value : queues) {
        const auto queue = value.toObject();
        auto *item = new QListWidgetItem(queue.value("name").toString(), queues_);
        item->setData(QueueIdRole, queue.value("id").toInt());
        item->setData(QueueRunningRole, queue.value("running").toBool());
        item->setData(QueueConcurrencyRole, queue.value("concurrency").toInt(3));
        item->setData(DefaultQueueRole, queue.value("isDefault").toBool());
        item->setData(QueueQuotaRole, queue.toVariantMap());
        if (item->data(QueueIdRole) == selected) queues_->setCurrentItem(item);
    }
    if (const auto *item = queues_->currentItem()) listHeading_->setText(item->text());
    if (selected.isValid() && !queues_->currentItem()) {
        filter_->setQueue({});
        findChild<QListWidget *>(QStringLiteral("downloadViews"))->setCurrentRow(0);
        updateEmptyState();
    }
    updateActions();
}

void MainWindow::closeEvent(QCloseEvent *event)
{
    if (details_) details_->close();
    QSettings().setValue(QStringLiteral("downloads/columns-v1"),
                         table_->horizontalHeader()->saveState());
    QMainWindow::closeEvent(event);
}

void MainWindow::showDownloadDetails(const QModelIndex &index)
{
    const auto id = index.siblingAtColumn(0).data(JobIdRole).toString();
    if (id.isEmpty()) return;
    const bool changed = detailsJobId_ != id;
    detailsJobId_ = id;
    if (!details_) {
        details_ = new QDialog(this);
        details_->setWindowTitle(tr("Download Details"));
        details_->setMinimumWidth(370);
        auto *layout = new QVBoxLayout(details_);
        detailsSummary_ = new QLabel(details_);
        detailsSummary_->setTextFormat(Qt::PlainText);
        detailsSummary_->setTextInteractionFlags(Qt::TextSelectableByMouse);
        detailsSummary_->setWordWrap(true);
        layout->addWidget(detailsSummary_);
        detailsProgress_ = new QProgressBar(details_);
        detailsProgress_->setAccessibleName(tr("Download progress"));
        layout->addWidget(detailsProgress_);
        auto *tabs = new QTabWidget(details_);
        layout->addWidget(tabs);
        const auto addTable = [tabs](const QString &title, const QStringList &columns,
                                    QLabel *&status, QTableWidget *&table) {
            auto *page = new QWidget(tabs);
            auto *content = new QVBoxLayout(page);
            status = new QLabel(page);
            content->addWidget(status);
            table = new QTableWidget(0, columns.size(), page);
            table->setAccessibleName(title);
            table->setHorizontalHeaderLabels(columns);
            table->horizontalHeader()->setSectionResizeMode(QHeaderView::Stretch);
            table->verticalHeader()->hide();
            table->setEditTriggers(QAbstractItemView::NoEditTriggers);
            content->addWidget(table);
            tabs->addTab(page, title);
        };
        addTable(tr("Segments"), {tr("Byte range"), tr("Downloaded"), tr("Progress")},
                 segmentStatus_, segments_);
        auto *segmentsLayout = qobject_cast<QVBoxLayout *>(segments_->parentWidget()->layout());
        segmentMap_ = new SegmentMap(segments_->parentWidget());
        segmentsLayout->insertWidget(1, segmentMap_);
        addTable(tr("Connections"), {tr("State"), tr("Byte range"), tr("Received"), tr("Rate")},
                 connectionDetailsStatus_, connections_);
        auto *historyPage = new QWidget(tabs);
        auto *historyLayout = new QVBoxLayout(historyPage);
        speedHistoryStatus_ = new QLabel(historyPage);
        historyLayout->addWidget(speedHistoryStatus_);
        detailsSpeedGraph_ = new SpeedGraph(historyPage);
        detailsSpeedGraph_->setExpanded(true);
        historyLayout->addWidget(detailsSpeedGraph_);
        historyLayout->addStretch();
        tabs->addTab(historyPage, tr("Speed History"));
        detailsCompletionDialog_ = new QCheckBox(tr("Show completion dialog for this download"), details_);
        layout->addWidget(detailsCompletionDialog_);
        connect(detailsCompletionDialog_, &QCheckBox::clicked, this, [this](bool checked) {
            const QJsonObject payload{{"jobId", detailsJobId_}, {"showCompletionDialog", checked}};
            emit commandRequested("edit-completion-dialog",
                QString::fromUtf8(QJsonDocument(payload).toJson(QJsonDocument::Compact)));
        });
        details_->resize(520, 420);
        auto *buttons = new QDialogButtonBox(QDialogButtonBox::Close, details_);
        detailsStop_ = buttons->addButton(tr("Stop"), QDialogButtonBox::ActionRole);
        detailsResume_ = buttons->addButton(tr("Resume"), QDialogButtonBox::ActionRole);
        detailsCancel_ = buttons->addButton(tr("Cancel Download"), QDialogButtonBox::ActionRole);
        connect(detailsStop_, &QPushButton::clicked, this, [this] {
            emit commandRequested("stop", detailsJobId_);
        });
        connect(detailsResume_, &QPushButton::clicked, this, [this] {
            emit commandRequested("resume", detailsJobId_);
        });
        connect(detailsCancel_, &QPushButton::clicked, this, [this] {
            const auto id = detailsJobId_;
            if (QMessageBox::question(details_, tr("Cancel download?"),
                    tr("Cancel this transfer and delete its partial file?"),
                    QMessageBox::Yes | QMessageBox::No, QMessageBox::No) == QMessageBox::Yes &&
                id == detailsJobId_ && detailsCancel_->isEnabled())
                emit commandRequested("cancel-download", id);
        });
        connect(buttons, &QDialogButtonBox::rejected, details_, &QDialog::close);
        layout->addWidget(buttons);
    }
    if (changed) {
        segments_->setRowCount(0);
        segmentMap_->reset();
        segmentStatus_->setText(tr("Loading saved segments…"));
        connections_->setRowCount(0);
        connectionDetailsStatus_->setText(tr("Loading active connections…"));
        speedHistoryStatus_->setText(tr("Loading speed history…"));
        detailsSpeedGraph_->setHistory({}, 1);
    }
    details_->show();
    refreshDownloadDetails();
    details_->raise();
    details_->activateWindow();
}

void MainWindow::refreshDownloadDetails()
{
    if (!details_ || !details_->isVisible()) return;
    int row = 0;
    while (row < model_->rowCount() && model_->item(row, 0)->data(JobIdRole).toString() != detailsJobId_)
        ++row;
    if (row == model_->rowCount()) {
        detailsSummary_->setText(tr("This download is no longer in the list."));
        detailsProgress_->hide();
        detailsStop_->setEnabled(false);
        detailsResume_->setEnabled(false);
        detailsCancel_->setEnabled(false);
        detailsCompletionDialog_->setEnabled(false);
        segments_->setRowCount(0);
        segmentMap_->reset();
        segmentStatus_->clear();
        connections_->setRowCount(0);
        connectionDetailsStatus_->clear();
        speedHistoryStatus_->clear();
        detailsSpeedGraph_->setHistory({}, 1);
        detailsJobId_.clear();
        return;
    }

    const auto *name = model_->item(row, 0);
    const bool ready = connected_ && !commandBusy_;
    detailsStop_->setEnabled(ready && name->data(CanPauseRole).toBool());
    detailsResume_->setEnabled(ready && name->data(CanResumeRole).toBool());
    detailsCancel_->setEnabled(ready && name->data(CanCancelRole).toBool());
    detailsCompletionDialog_->setEnabled(ready && !name->data(TerminalRole).toBool());
    detailsCompletionDialog_->setChecked(name->data(CompletionDialogRole).toBool());
    const auto *status = model_->item(row, 3);
    segmentMap_->setTransfer(model_->item(row, 2)->data(SortValueRole).toLongLong(),
                             name->data(ReceivedRole).toLongLong());
    const int progress = status->data(ProgressRole).toInt();
    const auto text = [this, row](int column) {
        const auto value = model_->item(row, column)->text();
        return value.isEmpty() ? QStringLiteral("—") : value;
    };
    const QLocale locale;
    QStringList lines{
        name->text(),
        tr("Status: %1").arg(status->text()),
        tr("Downloaded: %1 of %2").arg(locale.formattedDataSize(name->data(ReceivedRole).toLongLong()), text(2)),
        tr("Transfer rate: %1").arg(text(5)),
        tr("Time left: %1").arg(text(4)),
        tr("Active connections: %1").arg(name->data(ConnectionsRole).toInt())
    };
    if (!connected_) {
        lines.append(tr("Host disconnected. Showing last received values."));
        segmentStatus_->setText(tr("Last received segments"));
        connectionDetailsStatus_->setText(tr("Last received connections"));
        speedHistoryStatus_->setText(tr("Last received speed history"));
    }
    detailsSummary_->setText(lines.join(QLatin1Char('\n')));
    detailsProgress_->setVisible(progress >= 0);
    if (progress >= 0) detailsProgress_->setValue(progress);
}

QString MainWindow::visibleDetailsJob() const
{
    return connected_ && details_ && details_->isVisible() ? detailsJobId_ : QString();
}

void MainWindow::setDownloadSegments(const QString &jobId, const QJsonArray &saved, const QJsonArray &connections, bool available)
{
    if (jobId.isEmpty() || jobId != visibleDetailsJob()) return;
    if (!available) {
        segmentStatus_->setText(tr("Segment data unavailable. Retrying…"));
        return;
    }
    const auto segments = segmentMap_->mergeProgress(saved, connections);
    segmentStatus_->setText(segments.isEmpty() ? tr("No segment data") : tr("Segment progress"));
    segmentMap_->setSegments(segments);
    segments_->setRowCount(segments.size());
    const QLocale locale;
    for (int row = 0; row < segments.size(); ++row) {
        const auto segment = segments[row].toObject();
        const QStringList fields{
            tr("%1–%2").arg(locale.toString(segment.value("start").toInteger()),
                            locale.toString(segment.value("end").toInteger())),
            locale.formattedDataSize(segment.value("completed").toInteger()),
            tr("%1%").arg(qRound(segment.value("fraction").toDouble() * 100))
        };
        for (int col = 0; col < fields.size(); ++col) {
            if (auto *item = segments_->item(row, col)) item->setText(fields[col]);
            else segments_->setItem(row, col, new QTableWidgetItem(fields[col]));
        }
    }
}

void MainWindow::setDownloadConnections(const QString &jobId, const QJsonArray &connections, bool available)
{
    if (jobId.isEmpty() || jobId != visibleDetailsJob()) return;
    if (!available) {
        connectionDetailsStatus_->setText(tr("Connection data unavailable. Retrying…"));
        return;
    }
    connectionDetailsStatus_->setText(connections.isEmpty() ? tr("No active connections") : tr("Live connections"));
    connections_->setRowCount(connections.size());
    const QLocale locale;
    for (int row = 0; row < connections.size(); ++row) {
        const auto connection = connections[row].toObject();
        const auto start = connection.value("rangeStart");
        const auto end = connection.value("rangeEnd");
        const auto range = start.isDouble() && end.isDouble()
            ? tr("%1–%2").arg(locale.toString(start.toInteger()), locale.toString(end.toInteger()))
            : tr("—");
        const QStringList fields{
            connection.value("state").toString(), range,
            locale.formattedDataSize(connection.value("receivedBytes").toInteger()),
            tr("%1/s").arg(locale.formattedDataSize(connection.value("bytesPerSecond").toInteger()))
        };
        for (int col = 0; col < fields.size(); ++col) {
            if (auto *item = connections_->item(row, col)) item->setText(fields[col]);
            else connections_->setItem(row, col, new QTableWidgetItem(fields[col]));
        }
    }
}

void MainWindow::setDownloadSpeedHistory(const QString &jobId, const QJsonObject &history)
{
    if (jobId.isEmpty() || jobId != visibleDetailsJob()) return;
    const auto points = history.value("points");
    if (!points.isArray()) {
        speedHistoryStatus_->setText(tr("Speed history unavailable"));
        return;
    }
    const auto rates = points.toArray();
    speedHistoryStatus_->setText(rates.isEmpty() ? tr("No recorded speeds yet") : tr("Last minute"));
    detailsSpeedGraph_->setHistory(rates, history.value("interval").toInt(1));
}

void MainWindow::showCompletion(const QJsonObject &job)
{
    const auto path = job.value("completedFile").toString();
    if (path.isEmpty()) return;

    auto *dialog = new QDialog(this);
    dialog->setAttribute(Qt::WA_DeleteOnClose);
    dialog->setWindowTitle(tr("Download Complete"));
    dialog->setMinimumWidth(420);
    auto *layout = new QVBoxLayout(dialog);
    auto *summary = new QLabel(dialog);
    summary->setTextFormat(Qt::PlainText);
    summary->setTextInteractionFlags(Qt::TextSelectableByMouse);
    summary->setWordWrap(true);
    const QLocale locale;
    const auto completed = QDateTime::fromString(job.value("completedAt").toString(), Qt::ISODate);
    summary->setText(tr("%1\n%2 downloaded\nFinished: %3\nSaved to: %4")
        .arg(job.value("name").toString(), locale.formattedDataSize(job.value("received").toInteger()),
             completed.isValid() ? locale.toString(completed.toLocalTime(), QLocale::ShortFormat) : tr("—"),
             path));
    layout->addWidget(summary);

    auto *buttons = new QDialogButtonBox(QDialogButtonBox::Close, dialog);
    auto *open = buttons->addButton(tr("Open File"), QDialogButtonBox::ActionRole);
    auto *folder = buttons->addButton(tr("Open Folder"), QDialogButtonBox::ActionRole);
    connect(open, &QPushButton::clicked, dialog, [dialog, path] { openCompletedPath(dialog, path, false); });
    connect(folder, &QPushButton::clicked, dialog, [dialog, path] { openCompletedPath(dialog, path, true); });
    connect(buttons, &QDialogButtonBox::rejected, dialog, &QDialog::close);
    layout->addWidget(buttons);
    dialog->show();
}

void MainWindow::sendQueueEdit(const QJsonObject &payload)
{
    emit commandRequested("edit-queue",
        QString::fromUtf8(QJsonDocument(payload).toJson(QJsonDocument::Compact)));
}

void MainWindow::editQueue(bool create)
{
    const auto *queue = queues_->currentItem();
    if (!create && !queue) return;
    const int id = create ? 0 : queue->data(QueueIdRole).toInt();
    const auto title = create ? tr("New Queue") : tr("Edit Queue");
    QDialog dialog(this);
    dialog.setWindowTitle(title);
    auto *form = new QFormLayout(&dialog);
    auto *name = new QLineEdit(create ? QString() : queue->text(), &dialog);
    auto *concurrency = new QSpinBox(&dialog);
    concurrency->setRange(1, 16);
    concurrency->setValue(create ? 3 : queue->data(QueueConcurrencyRole).toInt());
    form->addRow(tr("Queue name"), name);
    form->addRow(tr("Simultaneous downloads"), concurrency);
    const auto quota = create ? QVariantMap{} : queue->data(QueueQuotaRole).toMap();
    auto *quotaEnabled = new QCheckBox(tr("Stop the queue when its allowance is used"), &dialog);
    quotaEnabled->setChecked(quota.value("quotaEnabled", false).toBool());
    auto *quotaBytes = new QLineEdit(QString::number(quota.value("quotaBytes", 200 * 1024 * 1024).toLongLong()), &dialog);
    auto *quotaHours = new QSpinBox(&dialog);
    quotaHours->setRange(1, 8760);
    quotaHours->setValue(quota.value("quotaHours", 5).toInt());
    form->addRow(tr("Quota"), quotaEnabled);
    form->addRow(tr("Allowance (bytes)"), quotaBytes);
    form->addRow(tr("Window (hours)"), quotaHours);
    if (!create) {
        const auto used = QLocale().formattedDataSize(quota.value("quotaUsed").toLongLong());
        form->addRow(tr("Used in current window"), new QLabel(used, &dialog));
    }
    auto *buttons = new QDialogButtonBox(QDialogButtonBox::Save | QDialogButtonBox::Cancel, &dialog);
    auto *save = buttons->button(QDialogButtonBox::Save);
    const auto validate = [=] {
        const bool enabled = quotaEnabled->isChecked();
        quotaBytes->setEnabled(enabled);
        quotaHours->setEnabled(enabled);
        bool valid = false;
        const auto bytes = quotaBytes->text().trimmed().toLongLong(&valid);
        save->setEnabled(!name->text().trimmed().isEmpty() &&
                         (!enabled || (valid && bytes >= 1 && bytes <= 10LL * 1024 * 1024 * 1024 * 1024)));
    };
    connect(name, &QLineEdit::textChanged, save, validate);
    connect(quotaBytes, &QLineEdit::textChanged, save, validate);
    connect(quotaEnabled, &QCheckBox::toggled, save, validate);
    validate();
    connect(buttons, &QDialogButtonBox::accepted, &dialog, &QDialog::accept);
    connect(buttons, &QDialogButtonBox::rejected, &dialog, &QDialog::reject);
    form->addRow(buttons);
    if (dialog.exec() != QDialog::Accepted) return;
    QJsonObject payload{{"action", create ? "create" : "policy"}, {"queueId", id},
                        {"name", name->text().trimmed()}, {"maxConcurrentDownloads", concurrency->value()},
                        {"quotaEnabled", quotaEnabled->isChecked()}};
    if (quotaEnabled->isChecked()) {
        payload.insert("quotaBytes", quotaBytes->text().trimmed().toLongLong());
        payload.insert("quotaWindowHours", quotaHours->value());
    }
    sendQueueEdit(payload);
}

void MainWindow::setTorBoxAvailable(bool available)
{
    torboxAvailable_ = available;
    updateActions();
}

bool MainWindow::handleToolResult(const QString &command, const QJsonObject &result)
{
    if (command.startsWith(QStringLiteral("torbox-"))) {
        if (torboxDialog_) torboxDialog_->handleResult(command, result);
        return true;
    }
    if (command == "options-save") {
        if (optionsDialog_) optionsDialog_->saveResult(result);
        return true;
    }
    if (command != "options-get" || !result.value("ok").toBool()) return false;

    optionsDialog_ = new OptionsDialog(this, result.value("options").toObject(), [this](const QJsonObject &edit) {
        if (!connected_ || commandBusy_) {
            optionsDialog_->saveResult({{"ok", false}, {"error", tr("Host unavailable or busy.")}});
            return;
        }
        emit commandRequested("options-save", QString::fromUtf8(QJsonDocument(edit).toJson(QJsonDocument::Compact)));
    });
    optionsDialog_->setAttribute(Qt::WA_DeleteOnClose);
    optionsDialog_->show();
    return true;
}
